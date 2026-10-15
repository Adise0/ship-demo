using System.Collections.Generic;
using FishNet;
using FishNet.Managing;
using FishNet.Managing.Timing;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// A ship that flies a deterministic path and owns its own physics scene for its interior.
/// Players inside the ship are simulated in that scene, in ship-local coordinates, so ship
/// motion never disturbs them.
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

    [Header("Path")]
    [SerializeField]
    private Vector3 _pathCenter = new(0f, 20f, 0f);
    [SerializeField]
    private float _pathRadius = 60f;
    // Seconds per lap.
    [SerializeField]
    private float _pathPeriod = 90f;
    [SerializeField]
    private float _rollDegrees = 12f;
    [SerializeField]
    private float _bobHeight = 3f;
    // Forward/back surge along the path, so there is real acceleration to feel inside.
    [SerializeField]
    private float _surgeDistance = 8f;
    [SerializeField]
    private float _surgePeriod = 12f;

    public int Id => _id;
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

    private void Awake()
    {
        _ships[_id] = this;

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
    /// Ship pose at a server-aligned tick. Pure function of time, so every peer agrees.
    /// </summary>
    public void GetPose(double tick, out Vector3 position, out Quaternion rotation)
    {
        double seconds = tick * ShipClock.TickDelta;
        float angle = (float)(seconds / _pathPeriod * 2d * Mathf.PI);

        position = _pathCenter
            + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * _pathRadius
            + Vector3.up * (Mathf.Sin((float)seconds * 0.4f) * _bobHeight);

        Vector3 forward = new(-Mathf.Sin(angle), 0f, Mathf.Cos(angle));
        float surge = Mathf.Sin((float)(seconds / _surgePeriod * 2d * Mathf.PI)) * _surgeDistance;
        position += forward * surge;

        float roll = Mathf.Sin((float)seconds * 0.3f) * _rollDegrees;
        rotation = Quaternion.LookRotation(forward) * Quaternion.Euler(0f, 0f, roll);
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
