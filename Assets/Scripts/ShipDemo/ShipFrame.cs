using System.Collections.Generic;
using FishNet;
using FishNet.Managing;
using FishNet.Managing.Timing;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// What the helm asks of the ship: a forward speed to hold and turn rates (-1..1 per axis:
/// pitch, yaw, roll).
/// </summary>
public struct HelmCommand
{
    public float Speed;
    public Vector3 Turn;

    public bool Equals(HelmCommand other) => Speed == other.Speed && Turn == other.Turn;
}

public struct ShipMotionState
{
    public Vector3 Position;
    public Quaternion Rotation;
    // World space.
    public Vector3 Velocity;
    // Ship-local, degrees per second.
    public Vector3 AngularVelocity;
}

/// <summary>
/// A ship flown by helm commands that owns its own physics scene for its interior. Players
/// inside the ship are simulated in that scene, in ship-local coordinates, so ship motion never
/// disturbs them.
/// Commands are scheduled for a future tick and sent to every peer, so the ship's pose is a
/// pure function of the command timeline and every peer computes the same pose for a tick.
/// </summary>
public class ShipFrame : MonoBehaviour
{
    private static readonly Dictionary<int, ShipFrame> _ships = new();
    public static IEnumerable<ShipFrame> All => _ships.Values;
    public static ShipFrame Get(int id) => _ships.TryGetValue(id, out ShipFrame ship) ? ship : null;

    // Must be unique and identical on every peer.
    [SerializeField]
    private int _id;
    // Child holding the hull colliders and visuals.
    [SerializeField]
    private Transform _hull;
    // Walkable interior volume in ship-local space. Players inside it use the ship's physics scene.
    [SerializeField]
    private Bounds _interior = new(new Vector3(0f, 2f, 0f), new Vector3(12f, 4f, 20f));
    [SerializeField]
    private Vector3 _spawnLocal = new(0f, 1f, 4f);
    // Within this distance of the hull (outside the interior) players also use the ship's
    // physics scene, so they can stand on and move along the outside of the hull.
    [SerializeField]
    private float _hullZoneDistance = 3f;

    [Header("Gravity")]
    // Artificial gravity only exists inside these ship-local volumes. Elsewhere (zero-g bay,
    // hull exterior) players float or use mag boots.
    [SerializeField]
    private Bounds[] _gravityZones = { new(new Vector3(0f, 2f, 3.5f), new Vector3(12f, 4.5f, 13f)) };
    [SerializeField]
    private float _gravityStrength = 15f;
    // 1 = ship motion is fully cancelled inside; 0 = floating bodies feel every burn and turn.
    [SerializeField, Range(0f, 1f)]
    private float _inertialDampening = 0.25f;

    [Header("Flight")]
    [SerializeField]
    private float _maxSpeed = 25f;
    [SerializeField]
    private float _maxReverseSpeed = 8f;
    [SerializeField]
    private float _acceleration = 3f;
    // Degrees per second at full turn input: pitch, yaw, roll.
    [SerializeField]
    private Vector3 _turnRates = new(8f, 10f, 10f);
    // Degrees per second squared.
    [SerializeField]
    private float _turnAcceleration = 6f;

    public int Id => _id;
    public float MaxSpeed => _maxSpeed;
    public float MaxReverseSpeed => _maxReverseSpeed;
    public Vector3 SpawnLocal => _spawnLocal;
    public float HullZoneDistance => _hullZoneDistance;
    public float InertialDampening => _inertialDampening;
    public Scene InteriorScene { get; private set; }
    public PhysicsScene InteriorPhysics { get; private set; }

    // Kinematic copy of the hull colliders in the world scene, for players in EVA.
    private Rigidbody _worldHull;
    // Root of the hull collider copy inside the interior scene; used to tell hull from players.
    private Transform _interiorHull;
    private TimeManager _timeManager;
    private static readonly Collider[] _overlapResults = new Collider[32];

    // Motion state per tick, in a ring buffer. Ticks _baseTick.._computedTick are valid.
    private const int HistorySize = 1024;
    private readonly ShipMotionState[] _history = new ShipMotionState[HistorySize];
    private bool _historyStarted;
    private long _baseTick;
    private long _computedTick;
    private ShipMotionState _initialState;

    private struct ScheduledCommand
    {
        public long Tick;
        public HelmCommand Command;
        public byte Sequence;
        // Applied locally by the helm's own client before the server confirmed it.
        public bool Predicted;
    }
    // Command in effect at _baseTick, and commands after it in tick order.
    private HelmCommand _baseCommand;
    private readonly List<ScheduledCommand> _commands = new();

    private void Awake()
    {
        _ships[_id] = this;
        _initialState = new ShipMotionState { Position = transform.position, Rotation = transform.rotation };

        InteriorScene = SceneManager.CreateScene($"ShipInterior_{_id}", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
        InteriorPhysics = InteriorScene.GetPhysicsScene();

        // Interior copy: never moves, lives at the origin of its own physics scene.
        GameObject interior = CreateColliderCopy("InteriorColliders");
        SceneManager.MoveGameObjectToScene(interior, InteriorScene);
        _interiorHull = interior.transform;

        // World copy: follows the ship path each tick.
        GameObject world = CreateColliderCopy("WorldHull");
        _worldHull = world.AddComponent<Rigidbody>();
        _worldHull.isKinematic = true;
        _worldHull.interpolation = RigidbodyInterpolation.None;

        // The visible hull only renders; physics uses the copies above.
        foreach (Collider c in _hull.GetComponentsInChildren<Collider>())
            c.enabled = false;
    }

    private void Start()
    {
        NetworkManager networkManager = InstanceFinder.NetworkManager;
        ShipClock.Initialize(networkManager);
        _timeManager = networkManager.TimeManager;
        _timeManager.OnPrePhysicsSimulation += TimeManager_OnPrePhysicsSimulation;
        _timeManager.OnPostPhysicsSimulation += TimeManager_OnPostPhysicsSimulation;
    }

    private void OnDestroy()
    {
        _ships.Remove(_id);
        if (_timeManager != null)
        {
            _timeManager.OnPrePhysicsSimulation -= TimeManager_OnPrePhysicsSimulation;
            _timeManager.OnPostPhysicsSimulation -= TimeManager_OnPostPhysicsSimulation;
        }
        if (_worldHull != null)
            Destroy(_worldHull.gameObject);
        if (InteriorScene.IsValid() && InteriorScene.isLoaded)
            SceneManager.UnloadSceneAsync(InteriorScene);
    }

    /// <summary>
    /// Copies the hull's colliders (and nothing else) into a new root object,
    /// laid out in ship-local space.
    /// </summary>
    private GameObject CreateColliderCopy(string name)
    {
        GameObject root = new(name);
        foreach (Collider source in _hull.GetComponentsInChildren<Collider>())
        {
            GameObject part = new(source.name);
            part.transform.SetParent(root.transform, false);
            Matrix4x4 m = transform.worldToLocalMatrix * source.transform.localToWorldMatrix;
            part.transform.SetLocalPositionAndRotation(m.GetPosition(), m.rotation);
            part.transform.localScale = m.lossyScale;

            switch (source)
            {
                case BoxCollider box:
                    BoxCollider b = part.AddComponent<BoxCollider>();
                    b.center = box.center;
                    b.size = box.size;
                    break;
                case SphereCollider sphere:
                    SphereCollider s = part.AddComponent<SphereCollider>();
                    s.center = sphere.center;
                    s.radius = sphere.radius;
                    break;
                case CapsuleCollider capsule:
                    CapsuleCollider c = part.AddComponent<CapsuleCollider>();
                    c.center = capsule.center;
                    c.radius = capsule.radius;
                    c.height = capsule.height;
                    c.direction = capsule.direction;
                    break;
                case MeshCollider mesh:
                    MeshCollider mc = part.AddComponent<MeshCollider>();
                    mc.sharedMesh = mesh.sharedMesh;
                    mc.convex = mesh.convex;
                    break;
            }
            part.GetComponent<Collider>().sharedMaterial = source.sharedMaterial;
        }
        return root;
    }

    private void TimeManager_OnPrePhysicsSimulation(float delta)
    {
        // Sweep the world hull from last tick's pose to this tick's pose so it pushes EVA players.
        long tick = ShipClock.SimTick;
        GetPose(tick - 1, out Vector3 fromPos, out Quaternion fromRot);
        GetPose(tick, out Vector3 toPos, out Quaternion toRot);
        _worldHull.position = fromPos;
        _worldHull.rotation = fromRot;
        _worldHull.MovePosition(toPos);
        _worldHull.MoveRotation(toRot);
    }

    private void TimeManager_OnPostPhysicsSimulation(float delta)
    {
        // FishNet steps the default scene (also during replays); step the interior alongside it.
        InteriorPhysics.Simulate(delta);
    }

    private void LateUpdate()
    {
        if (_timeManager == null || !_timeManager.NetworkManager.IsClientStarted)
            return;

        GetPose(ShipClock.RenderTick, out Vector3 pos, out Quaternion rot);
        transform.SetPositionAndRotation(pos, rot);
    }

    /// <summary>
    /// Ship pose at a server-aligned tick, interpolated between ticks. Every peer that knows the
    /// same commands computes the same pose. Ticks past the last known command extrapolate it.
    /// </summary>
    public void GetPose(double tick, out Vector3 position, out Quaternion rotation)
    {
        long t0 = (long)System.Math.Floor(tick);
        float alpha = (float)(tick - t0);
        ShipMotionState a = GetState(t0);
        ShipMotionState b = GetState(t0 + 1);
        position = Vector3.Lerp(a.Position, b.Position, alpha);
        rotation = Quaternion.Slerp(a.Rotation, b.Rotation, alpha);
    }

    /// <summary>
    /// Motion state at a tick. Ticks older than the kept history return the oldest kept state.
    /// </summary>
    public ShipMotionState GetState(long tick)
    {
        if (!_historyStarted)
        {
            // Nothing known yet (a client before its first keyframe): start at rest where the
            // ship was placed, a little in the past so replays have history.
            _historyStarted = true;
            _baseTick = tick - 64;
            _computedTick = _baseTick;
            _history[Index(_baseTick)] = _initialState;
            _baseCommand = default;
        }

        if (tick > _computedTick)
        {
            // Far ahead (e.g. a long stall): skip the gap instead of stepping through all of it.
            if (tick - _computedTick > HistorySize / 2)
                Rebase(tick - 1, GetState(_computedTick), CommandAt(_computedTick));
            while (_computedTick < tick)
            {
                ShipMotionState next = Step(_history[Index(_computedTick)], CommandAt(_computedTick), (float)ShipClock.TickDelta);
                _computedTick++;
                _history[Index(_computedTick)] = next;
            }
            // Keep the history window inside the ring buffer.
            long oldest = _computedTick - HistorySize + 1;
            if (_baseTick < oldest)
                AdvanceBase(oldest);
        }

        if (tick < _baseTick)
            tick = _baseTick;
        return _history[Index(tick)];
    }

    /// <summary>
    /// Schedules a helm command. Authoritative commands come from the server and replace the
    /// helm client's own predicted copy (same sequence). Predicted ones are applied by the helm
    /// client right away so its controls respond without waiting for the round trip.
    /// </summary>
    public void ApplyCommand(long tick, HelmCommand command, byte sequence, bool predicted)
    {
        long earliest = tick;
        if (!predicted)
        {
            for (int i = _commands.Count - 1; i >= 0; i--)
            {
                if (_commands[i].Predicted && _commands[i].Sequence == sequence)
                {
                    earliest = System.Math.Min(earliest, _commands[i].Tick);
                    _commands.RemoveAt(i);
                }
            }
        }

        if (_historyStarted && tick <= _baseTick)
        {
            _baseCommand = command;
        }
        else
        {
            int index = _commands.Count;
            while (index > 0 && _commands[index - 1].Tick > tick)
                index--;
            _commands.Insert(index, new ScheduledCommand { Tick = tick, Command = command, Sequence = sequence, Predicted = predicted });
        }
        Invalidate(earliest);
    }

    /// <summary>
    /// Authoritative motion state from the server at a tick, with the command in effect then.
    /// Corrects any drift and anything this peer predicted that the server didn't confirm.
    /// </summary>
    public void ApplyKeyframe(long tick, ShipMotionState state, HelmCommand command)
    {
        // Out of the kept window (first keyframe, or a long stall): start over from this state.
        if (!_historyStarted || tick < _baseTick || tick > _computedTick + HistorySize / 2)
        {
            _historyStarted = true;
            Rebase(tick, state, command);
            return;
        }

        // Unconfirmed predictions at or before the keyframe didn't make it into the server's state.
        _commands.RemoveAll(c => c.Predicted && c.Tick <= tick);
        int index = _commands.Count;
        while (index > 0 && _commands[index - 1].Tick > tick)
            index--;
        _commands.Insert(index, new ScheduledCommand { Tick = tick, Command = command });

        if (tick > _computedTick)
            GetState(tick);
        _history[Index(tick)] = state;
        _computedTick = tick;
    }

    /// <summary>
    /// The command in effect at a tick.
    /// </summary>
    public HelmCommand CommandAt(long tick)
    {
        for (int i = _commands.Count - 1; i >= 0; i--)
        {
            if (_commands[i].Tick <= tick)
                return _commands[i].Command;
        }
        return _baseCommand;
    }

    private void Rebase(long tick, ShipMotionState state, HelmCommand command)
    {
        _baseTick = tick;
        _computedTick = tick;
        _history[Index(tick)] = state;
        _baseCommand = command;
        _commands.RemoveAll(c => c.Tick <= tick);
    }

    /// <summary>
    /// Drops history before a tick, folding the commands that took effect by then into the base.
    /// </summary>
    private void AdvanceBase(long tick)
    {
        _baseTick = tick;
        _baseCommand = CommandAt(tick);
        _commands.RemoveAll(c => c.Tick <= tick);
    }

    /// <summary>
    /// States after a tick are stale and get recomputed when next asked for.
    /// </summary>
    private void Invalidate(long tick)
    {
        if (!_historyStarted)
            return;
        _computedTick = System.Math.Max(System.Math.Min(_computedTick, tick), _baseTick);
    }

    private static int Index(long tick) => (int)(((tick % HistorySize) + HistorySize) % HistorySize);

    /// <summary>
    /// One tick of flight. The ship holds the commanded forward speed (its velocity follows its
    /// heading) and eases its turn rates toward the commanded ones.
    /// </summary>
    private ShipMotionState Step(ShipMotionState s, HelmCommand command, float delta)
    {
        Vector3 localVelocity = Quaternion.Inverse(s.Rotation) * s.Velocity;
        float speed = Mathf.Clamp(command.Speed, -_maxReverseSpeed, _maxSpeed);
        localVelocity = Vector3.MoveTowards(localVelocity, new Vector3(0f, 0f, speed), _acceleration * delta);

        Vector3 turn = Vector3.Scale(Vector3.Max(Vector3.Min(command.Turn, Vector3.one), -Vector3.one), _turnRates);
        s.AngularVelocity = Vector3.MoveTowards(s.AngularVelocity, turn, _turnAcceleration * delta);
        s.Rotation = (s.Rotation * Quaternion.Euler(s.AngularVelocity * delta)).normalized;

        s.Velocity = s.Rotation * localVelocity;
        s.Position += s.Velocity * delta;
        return s;
    }

    /// <summary>
    /// Places a pose in a frame's space (world, or a ship's local space) into the world, using
    /// the ship as drawn at renderTick.
    /// </summary>
    public static void FrameToWorld(int frame, double renderTick, ref Vector3 position, ref Quaternion rotation)
    {
        ShipFrame ship = Get(frame);
        if (ship == null)
            return;

        ship.GetPose(renderTick, out Vector3 shipPos, out Quaternion shipRot);
        position = shipPos + shipRot * position;
        rotation = shipRot * rotation;
    }

    /// <summary>
    /// Acceleration a free-floating body at a ship-local point feels relative to the ship, in
    /// ship-local space: the opposite of the ship's acceleration at that point (linear burn,
    /// angular acceleration and centrifugal) plus Coriolis for the body's own motion.
    /// Not scaled by dampening; callers apply that.
    /// </summary>
    public Vector3 GetFictitiousAcceleration(Vector3 localPoint, Vector3 localVelocity, double tick)
    {
        const double h = 1d;
        float dt = (float)(h * ShipClock.TickDelta);
        GetPose(tick - h, out Vector3 p0, out Quaternion r0);
        GetPose(tick, out Vector3 p1, out Quaternion r1);
        GetPose(tick + h, out Vector3 p2, out Quaternion r2);

        // World acceleration of the ship point the body is at.
        Vector3 a0 = p0 + r0 * localPoint;
        Vector3 a1 = p1 + r1 * localPoint;
        Vector3 a2 = p2 + r2 * localPoint;
        Vector3 pointAccel = (a2 - 2f * a1 + a0) / (dt * dt);

        // Ship angular velocity (world) for Coriolis.
        (r2 * Quaternion.Inverse(r0)).ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f)
            angle -= 360f;
        Vector3 omega = float.IsFinite(axis.x) ? axis * (angle * Mathf.Deg2Rad / (2f * dt)) : Vector3.zero;
        Vector3 coriolis = 2f * Vector3.Cross(omega, r1 * localVelocity);

        return Quaternion.Inverse(r1) * -(pointAccel + coriolis);
    }

    /// <summary>
    /// Artificial gravity at a ship-local point (zero outside every gravity zone).
    /// </summary>
    public Vector3 GetGravity(Vector3 localPoint)
    {
        foreach (Bounds zone in _gravityZones)
        {
            if (zone.Contains(localPoint))
                return Vector3.down * _gravityStrength;
        }
        return Vector3.zero;
    }

    /// <summary>
    /// Finds hull surfaces within reach of a ship-local point. Returns the distance to the
    /// nearest one and an up direction blended from all of them (weighted toward the closest),
    /// so edges and corners turn smoothly instead of flipping.
    /// </summary>
    public bool TryGetSurface(Vector3 localPoint, float reach, out Vector3 up, out float distance)
    {
        up = Vector3.zero;
        distance = float.MaxValue;
        int count = InteriorPhysics.OverlapSphere(localPoint, reach, _overlapResults, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Collider c = _overlapResults[i];
            if (!c.transform.IsChildOf(_interiorHull))
                continue;

            Vector3 closest = Physics.ClosestPoint(localPoint, c, c.transform.position, c.transform.rotation);
            Vector3 away = localPoint - closest;
            float d = away.magnitude;
            if (d < 0.0001f)
                continue;

            float weight = 1f / (d * d * d * d);
            up += away / d * weight;
            distance = Mathf.Min(distance, d);
        }

        if (up.sqrMagnitude < 0.0001f)
            return false;
        up.Normalize();
        return true;
    }

    /// <summary>
    /// True if a ship-local point is within distance of the hull.
    /// </summary>
    public bool IsNearHull(Vector3 localPoint, float distance)
    {
        int count = InteriorPhysics.OverlapSphere(localPoint, distance, _overlapResults, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            if (_overlapResults[i].transform.IsChildOf(_interiorHull))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True if a ship-local point belongs to this ship: inside the interior or near the hull.
    /// Margin grows (positive) or shrinks (negative) both, for enter/exit hysteresis.
    /// </summary>
    public bool IsInShipZone(Vector3 localPoint, float margin)
    {
        return ContainsLocal(localPoint, margin) || IsNearHull(localPoint, _hullZoneDistance + margin);
    }

    /// <summary>
    /// World-space velocity of a ship-local point at a tick, including rotation.
    /// </summary>
    public Vector3 GetPointVelocity(Vector3 localPoint, double tick)
    {
        const double h = 0.05d;
        GetPose(tick - h, out Vector3 p0, out Quaternion r0);
        GetPose(tick + h, out Vector3 p1, out Quaternion r1);
        Vector3 a = p0 + r0 * localPoint;
        Vector3 b = p1 + r1 * localPoint;
        return (b - a) / (float)(2d * h * ShipClock.TickDelta);
    }

    /// <summary>
    /// True if a ship-local point is inside the interior, grown or shrunk by margin.
    /// </summary>
    public bool ContainsLocal(Vector3 localPoint, float margin)
    {
        Bounds b = _interior;
        b.Expand(margin * 2f);
        return b.Contains(localPoint);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(_interior.center, _interior.size);
        Gizmos.color = Color.yellow;
        foreach (Bounds zone in _gravityZones)
            Gizmos.DrawWireCube(zone.center, zone.size);
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(_spawnLocal, 0.3f);
    }
}
