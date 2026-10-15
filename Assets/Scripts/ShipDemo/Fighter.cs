using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;
using UnityEngine.SceneManagement;
// NetworkBehaviour has its own SceneManager property (FishNet's); use Unity's explicitly.
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

public enum FighterMode : byte
{
    // Thrusters on every axis with flight assist: lets go of a stick and it stops (relative to
    // the frame it's in, so near the carrier it holds station with the carrier).
    Maneuver,
    // Main engine: strong forward thrust, no assist, momentum is kept.
    Engine,
}

public enum FighterDock : byte
{
    Free,
    // Being pulled onto the dock clamp.
    Capturing,
    Docked,
}

public struct FighterInput
{
    // -1..1: strafe right, up, forward.
    public Vector3 Thrust;
    // Keys, -1..1: yaw (right), roll (counter-clockwise). Eased by the turn acceleration.
    public float Yaw;
    public float Roll;
    // Mouse aim: degrees to turn this tick (pitch down, yaw right), applied directly.
    public Vector2 Aim;
    public bool Brake;
    public bool EngineToggle;
    public bool Release;
}

/// <summary>
/// Everything the pilot's prediction needs to rewind the fighter; carried in the pilot's reconcile.
/// </summary>
public struct FighterState
{
    public int Frame;
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Velocity;
    public Vector3 AngularVelocity;
    public FighterMode Mode;
    public FighterDock Dock;
    public int CaptureTicks;
    public Vector3 CaptureFromPosition;
    public Quaternion CaptureFromRotation;
    public bool CaptureArmed;
}

/// <summary>
/// Where an unpiloted fighter sits. Set by the server whenever the pilot leaves.
/// </summary>
public struct FighterParking
{
    public int Frame;
    public Vector3 Position;
    public Quaternion Rotation;
    public FighterDock Dock;
}

/// <summary>
/// A small craft that docks in a carrier's floor hatch. While piloted it's simulated as part of
/// its pilot's prediction (ShipPlayer calls Simulate from its replicate), so it responds
/// instantly for the pilot without any ownership change. Unpiloted, it's parked: kinematic, held
/// where the server last left it.
/// Like players, it's simulated in a ship's physics scene when near that ship, so the ship's
/// motion doesn't disturb close flying and docking.
/// </summary>
public class Fighter : NetworkBehaviour
{
    private const int WorldFrame = -1;

    private static readonly Dictionary<int, Fighter> _fighters = new();
    public static IEnumerable<Fighter> All => _fighters.Values;
    public static Fighter Get(int id) => _fighters.TryGetValue(id, out Fighter fighter) ? fighter : null;

    // Must be unique and identical on every peer.
    [SerializeField]
    private int _id;
    [SerializeField]
    private Vector3 _colliderSize = new(4.4f, 1.2f, 5.4f);
    [SerializeField]
    private float _mass = 20f;

    [Header("Carrier")]
    [SerializeField]
    private int _carrierId;
    // Dock clamp pose, carrier-local.
    [SerializeField]
    private Vector3 _dockPosition = new(0f, 1f, 2f);
    [SerializeField]
    private Vector3 _dockEuler;
    // Within this distance of the carrier's interior the fighter flies in the carrier's frame.
    [SerializeField]
    private float _frameDistance = 25f;

    [Header("Docking")]
    // Carrier-local volume under the hatch where capture can start.
    [SerializeField]
    private Bounds _captureZone = new(new Vector3(0f, -5.3f, 2f), new Vector3(8f, 9.4f, 10f));
    [SerializeField]
    private float _captureMaxSpeed = 4f;
    // Most the fighter's up can differ from the carrier's.
    [SerializeField]
    private float _captureMaxTilt = 30f;
    // Most the fighter's heading can differ from the carrier's.
    [SerializeField]
    private float _captureMaxHeading = 45f;
    [SerializeField]
    private float _captureDuration = 2.5f;
    // Carrier-local point straight below the dock that the pull lines up on before rising in.
    [SerializeField]
    private Vector3 _approachPosition = new(0f, -4f, 2f);
    // Speed the clamps drop the fighter out of the hatch at.
    [SerializeField]
    private float _releaseSpeed = 5f;

    [Header("Flight")]
    [SerializeField]
    private float _thrusterAccel = 12f;
    [SerializeField]
    private float _engineAccel = 35f;
    [SerializeField]
    private float _retroAccel = 12f;
    // Flight assist holds speed under this in maneuver mode.
    [SerializeField]
    private float _maneuverMaxSpeed = 25f;
    [SerializeField]
    private float _engineMaxSpeed = 250f;
    // Degrees per second at full input: pitch, yaw, roll.
    [SerializeField]
    private Vector3 _turnRates = new(70f, 55f, 120f);
    [SerializeField]
    private float _turnAccel = 400f;

    [Header("Pilot")]
    // Pilot's eye, fighter-local.
    [SerializeField]
    private Vector3 _cockpitPosition = new(0f, 0.75f, 0.9f);
    // Where the pilot steps out, fighter-local.
    [SerializeField]
    private Vector3 _exitPosition = new(4.2f, 0f, 0f);
    [SerializeField]
    private float _boardDistance = 4.5f;

    // ObjectId of the pilot, or -1. Who's allowed in; the simulation itself follows the pilot's state.
    private readonly SyncVar<int> _pilotId = new(-1);
    private readonly SyncVar<FighterParking> _parking = new();

    private Rigidbody _body;
    private Scene _worldScene;
    private int _frame = WorldFrame;
    // Piloting player on this peer, or null when parked.
    private ShipPlayer _pilot;
    private FighterMode _mode;
    private FighterDock _dock;
    private int _captureTicks;
    private Vector3 _captureFromPosition;
    private Quaternion _captureFromRotation;
    // Capture needs the fighter to have left the zone since it was last released.
    private bool _captureArmed = true;
    private Vector3 _angularVelocity;

    private struct Snapshot
    {
        public int Frame;
        public Vector3 Position;
        public Quaternion Rotation;
    }
    private Snapshot _previous;
    private Snapshot _current;

    public int Id => _id;
    public int Frame => _frame;
    public Vector3 Position => _body.position;
    public Quaternion Rotation => _body.rotation;
    public Vector3 Velocity => _body.isKinematic ? Vector3.zero : _body.linearVelocity;
    public FighterMode Mode => _mode;
    public FighterDock Dock => _dock;
    public float BoardDistance => _boardDistance;
    /// <summary>
    /// Most the mouse can turn the nose in one tick (pitch, yaw), in degrees.
    /// </summary>
    public Vector2 MaxAimPerTick(float delta) => new Vector2(_turnRates.x, _turnRates.y) * delta;
    public Vector3 CockpitPosition => _cockpitPosition;
    private Quaternion DockRotation => Quaternion.Euler(_dockEuler);

    public bool IsFreeFor(int playerObjectId) => _pilotId.Value == -1 || _pilotId.Value == playerObjectId;

    private void Awake()
    {
        _parking.OnChange += Parking_OnChange;
    }

    public override void OnStartNetwork()
    {
        _fighters[_id] = this;
        _worldScene = gameObject.scene;
        CreateBody();

        if (IsServerStarted)
        {
            _parking.Value = new FighterParking
            {
                Frame = _carrierId,
                Position = _dockPosition,
                Rotation = DockRotation,
                Dock = FighterDock.Docked,
            };
        }
        ApplyParking(_parking.Value);

        TimeManager.OnPostTick += TimeManager_OnPostTick;
        PredictionManager.OnPostReconcile += PredictionManager_OnPostReconcile;
    }

    public override void OnStopNetwork()
    {
        TimeManager.OnPostTick -= TimeManager_OnPostTick;
        PredictionManager.OnPostReconcile -= PredictionManager_OnPostReconcile;
        if (Get(_id) == this)
            _fighters.Remove(_id);
        if (_body != null)
            Destroy(_body.gameObject);
    }

    private void CreateBody()
    {
        GameObject go = new($"{name}_Body");
        UnitySceneManager.MoveGameObjectToScene(go, _worldScene);
        // This is a scene object, so it's reused when networking stops and starts again: forget
        // the last session, starting from the world frame the new body is in.
        _frame = WorldFrame;
        _pilot = null;
        _dock = FighterDock.Free;
        _mode = FighterMode.Maneuver;
        _captureArmed = true;
        _angularVelocity = Vector3.zero;

        BoxCollider box = go.AddComponent<BoxCollider>();
        box.size = _colliderSize;
        box.sharedMaterial = new PhysicsMaterial("Fighter")
        {
            dynamicFriction = 0f,
            staticFriction = 0f,
            frictionCombine = PhysicsMaterialCombine.Minimum,
            bounciness = 0f,
            bounceCombine = PhysicsMaterialCombine.Minimum,
        };

        _body = go.AddComponent<Rigidbody>();
        _body.mass = _mass;
        _body.useGravity = false;
        _body.freezeRotation = true;
        _body.isKinematic = true;
        _body.interpolation = RigidbodyInterpolation.None;
        _body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        _body.linearDamping = 0f;
        _body.angularDamping = 0f;
    }

    /// <summary>
    /// Moves the body into the world or a ship's physics scene.
    /// </summary>
    private void SetFrame(int frame)
    {
        if (frame == _frame)
            return;

        Scene target = _worldScene;
        if (frame != WorldFrame)
        {
            ShipFrame ship = ShipFrame.Get(frame);
            if (ship == null)
                frame = WorldFrame;
            else
                target = ship.InteriorScene;
        }

        _frame = frame;
        UnitySceneManager.MoveGameObjectToScene(_body.gameObject, target);
    }

    /// <summary>
    /// Physics only moves the fighter while it's piloted and flying free; docked, being captured
    /// or parked it's placed directly and pushes players rather than being pushed.
    /// </summary>
    private void RefreshKinematic()
    {
        bool kinematic = _pilot == null || _dock != FighterDock.Free;
        if (kinematic && !_body.isKinematic)
            _body.linearVelocity = Vector3.zero;
        _body.isKinematic = kinematic;
    }

    private void SetBody(Vector3 position, Quaternion rotation, Vector3 velocity)
    {
        _body.position = position;
        _body.rotation = rotation;
        if (!_body.isKinematic)
            _body.linearVelocity = velocity;
    }

    #region Piloting.
    public void BeginPiloting(ShipPlayer pilot)
    {
        if (_pilot == pilot)
            return;
        _pilot = pilot;
        RefreshKinematic();
        if (IsServerStarted)
            _pilotId.Value = pilot.ObjectId;
    }

    /// <summary>
    /// The pilot left (or was corrected out of the seat): park where the fighter is now.
    /// </summary>
    public void EndPiloting(ShipPlayer pilot)
    {
        if (_pilot != pilot)
            return;
        _pilot = null;
        if (_dock == FighterDock.Capturing)
            _dock = FighterDock.Free;
        _angularVelocity = Vector3.zero;
        _mode = FighterMode.Maneuver;
        RefreshKinematic();

        if (IsServerStarted)
        {
            _pilotId.Value = -1;
            _parking.Value = new FighterParking
            {
                Frame = _frame,
                Position = _body.position,
                Rotation = _body.rotation,
                Dock = _dock,
            };
        }
    }

    public bool CanExit => _dock != FighterDock.Capturing;

    /// <summary>
    /// Where the pilot appears on stepping out, in the fighter's frame.
    /// </summary>
    public Vector3 ExitPosition => _body.position + _body.rotation * _exitPosition;

    private void Parking_OnChange(FighterParking prev, FighterParking next, bool asServer)
    {
        // The pilot's own simulation drives the fighter while there is one.
        if (!asServer && _body != null && _pilot == null)
            ApplyParking(next);
    }

    private void ApplyParking(FighterParking parking)
    {
        // Default (never set) parking has a zero rotation.
        if (parking.Rotation.w == 0f && parking.Rotation.x == 0f && parking.Rotation.y == 0f && parking.Rotation.z == 0f)
            return;

        SetFrame(parking.Frame);
        _dock = parking.Dock;
        _mode = FighterMode.Maneuver;
        _angularVelocity = Vector3.zero;
        RefreshKinematic();
        SetBody(parking.Position, parking.Rotation, Vector3.zero);
        _current = TakeSnapshot();
        _previous = _current;
    }

    public FighterState GetState() => new()
    {
        Frame = _frame,
        Position = _body.position,
        Rotation = _body.rotation,
        Velocity = Velocity,
        AngularVelocity = _angularVelocity,
        Mode = _mode,
        Dock = _dock,
        CaptureTicks = _captureTicks,
        CaptureFromPosition = _captureFromPosition,
        CaptureFromRotation = _captureFromRotation,
        CaptureArmed = _captureArmed,
    };

    public void SetState(FighterState state)
    {
        SetFrame(state.Frame);
        _mode = state.Mode;
        _dock = state.Dock;
        _captureTicks = state.CaptureTicks;
        _captureFromPosition = state.CaptureFromPosition;
        _captureFromRotation = state.CaptureFromRotation;
        _captureArmed = state.CaptureArmed;
        _angularVelocity = state.AngularVelocity;
        RefreshKinematic();
        Quaternion rotation = state.Rotation;
        SetBody(state.Position, IsValid(rotation) ? rotation : Quaternion.identity, state.Velocity);
    }
    #endregion

    #region Simulation.
    /// <summary>
    /// One tick of the piloted fighter, before physics. Called from the pilot's replicate.
    /// </summary>
    public void Simulate(FighterInput input, long tick, float delta)
    {
        switch (_dock)
        {
            case FighterDock.Docked:
                SimulateDocked(input);
                break;
            case FighterDock.Capturing:
                SimulateCapture(input, delta);
                break;
            default:
                UpdateFrame(tick);
                Fly(input, tick, delta);
                TryStartCapture();
                break;
        }
    }

    private void SimulateDocked(FighterInput input)
    {
        SetFrame(_carrierId);
        _mode = FighterMode.Maneuver;
        _angularVelocity = Vector3.zero;
        SetBody(_dockPosition, DockRotation, Vector3.zero);

        if (input.Release)
        {
            // Clamps open: drop out through the hatch. Capture stays off until the fighter has
            // cleared the docking zone, or it would grab it straight back.
            _dock = FighterDock.Free;
            _captureArmed = false;
            RefreshKinematic();
            _body.linearVelocity = DockRotation * Vector3.down * _releaseSpeed;
        }
    }

    private void SimulateCapture(FighterInput input, float delta)
    {
        if (input.Release)
        {
            _dock = FighterDock.Free;
            _captureArmed = false;
            RefreshKinematic();
            return;
        }

        _captureTicks++;
        float t = Mathf.Clamp01(_captureTicks * delta / _captureDuration);
        CapturePose(t, out Vector3 position, out Quaternion rotation);
        SetBody(position, rotation, Vector3.zero);
        if (t >= 1f)
            _dock = FighterDock.Docked;
    }

    /// <summary>
    /// The tractor pull: first line up under the hatch and match the carrier's orientation, then
    /// rise straight up onto the clamp.
    /// </summary>
    private void CapturePose(float t, out Vector3 position, out Quaternion rotation)
    {
        const float alignShare = 0.6f;
        if (t < alignShare)
        {
            float s = Mathf.SmoothStep(0f, 1f, t / alignShare);
            position = Vector3.Lerp(_captureFromPosition, _approachPosition, s);
            rotation = Quaternion.Slerp(_captureFromRotation, DockRotation, s);
        }
        else
        {
            float s = Mathf.SmoothStep(0f, 1f, (t - alignShare) / (1f - alignShare));
            position = Vector3.Lerp(_approachPosition, _dockPosition, s);
            rotation = DockRotation;
        }
    }

    private void TryStartCapture()
    {
        // Re-arm once clear of the zone and the hatch above it, i.e. after a launch has dropped
        // out of the bottom or flown off to a side.
        Bounds launchArea = _captureZone;
        launchArea.Encapsulate(_dockPosition);
        if (_frame != _carrierId || !launchArea.Contains(_body.position))
        {
            _captureArmed = true;
            return;
        }
        if (!_captureArmed || !_captureZone.Contains(_body.position) || GetCaptureProblem() != null)
            return;

        _dock = FighterDock.Capturing;
        _captureTicks = 0;
        _captureFromPosition = _body.position;
        _captureFromRotation = _body.rotation;
        _angularVelocity = Vector3.zero;
        RefreshKinematic();
    }

    /// <summary>
    /// Why the fighter can't be captured right now (speed or alignment), or null if it can.
    /// </summary>
    private string GetCaptureProblem()
    {
        float speed = Velocity.magnitude;
        if (speed > _captureMaxSpeed)
            return $"too fast ({speed:0.0} m/s, need under {_captureMaxSpeed:0})";

        Quaternion dock = DockRotation;
        Vector3 dockUp = dock * Vector3.up;
        if (Vector3.Angle(_body.rotation * Vector3.up, dockUp) > _captureMaxTilt)
            return "level out with the carrier";
        Vector3 heading = Vector3.ProjectOnPlane(_body.rotation * Vector3.forward, dockUp);
        if (Vector3.Angle(heading, dock * Vector3.forward) > _captureMaxHeading)
            return "point the nose the same way as the carrier";
        return null;
    }

    private void Fly(FighterInput input, long tick, float delta)
    {
        if (input.EngineToggle)
            _mode = _mode == FighterMode.Engine ? FighterMode.Maneuver : FighterMode.Engine;

        Quaternion rotation = _body.rotation;
        Vector3 velocity = _body.linearVelocity;

        // Yaw and roll keys ease in and out; the mouse turns the nose directly, like aiming.
        Vector3 keyTurn = new(0f, Mathf.Clamp(input.Yaw, -1f, 1f) * _turnRates.y, Mathf.Clamp(input.Roll, -1f, 1f) * _turnRates.z);
        _angularVelocity = Vector3.MoveTowards(_angularVelocity, keyTurn, _turnAccel * delta);
        Vector2 aim = Vector2.Max(Vector2.Min(input.Aim, MaxAimPerTick(delta)), -MaxAimPerTick(delta));
        Vector3 turn = _angularVelocity * delta + new Vector3(aim.x, aim.y, 0f);
        rotation = (rotation * Quaternion.Euler(turn)).normalized;

        // Thrust in the fighter's axes. Velocity is kept in its frame, so turning doesn't turn it.
        Vector3 thrust = Clamp(input.Thrust);
        Vector3 local = Quaternion.Inverse(rotation) * velocity;
        if (_mode == FighterMode.Engine)
        {
            if (input.Brake)
            {
                local = Vector3.MoveTowards(local, Vector3.zero, _thrusterAccel * delta);
            }
            else
            {
                float forward = thrust.z > 0f ? _engineAccel : _retroAccel;
                local += new Vector3(thrust.x * _thrusterAccel, thrust.y * _thrusterAccel, thrust.z * forward) * delta;
            }
            local = Vector3.ClampMagnitude(local, _engineMaxSpeed);
        }
        else
        {
            // Flight assist: any axis without input (or all of them while braking) is brought to
            // rest. Not while launching (released and still in the docking zone), so the drop
            // carries the fighter clear of the hatch.
            bool launching = !_captureArmed && !input.Brake;
            float step = _thrusterAccel * delta;
            for (int axis = 0; axis < 3; axis++)
            {
                if (thrust[axis] != 0f && !input.Brake)
                    local[axis] += thrust[axis] * step;
                else if (!launching)
                    local[axis] = Mathf.MoveTowards(local[axis], 0f, step);
            }
            if (local.magnitude > _maneuverMaxSpeed)
                local = Vector3.MoveTowards(local, local.normalized * _maneuverMaxSpeed, step);
        }
        velocity = rotation * local;

        // In a ship's frame its maneuvers are felt like on a free-floating body.
        ShipFrame ship = ShipFrame.Get(_frame);
        if (ship != null)
            velocity += ship.GetFictitiousAcceleration(_body.position, velocity, tick) * delta;

        _body.rotation = rotation;
        _body.linearVelocity = velocity;
    }

    /// <summary>
    /// Moves between the world and a ship's frame on entering or leaving the space around it.
    /// </summary>
    private void UpdateFrame(long tick)
    {
        const float margin = 1f;

        if (_frame == WorldFrame)
        {
            foreach (ShipFrame ship in ShipFrame.All)
            {
                ship.GetPose(tick, out Vector3 shipPos, out Quaternion shipRot);
                Quaternion inverse = Quaternion.Inverse(shipRot);
                Vector3 localPos = inverse * (_body.position - shipPos);
                if (!ship.ContainsLocal(localPos, _frameDistance - margin))
                    continue;

                Vector3 relativeVel = _body.linearVelocity - ship.GetPointVelocity(localPos, tick);
                Quaternion localRot = inverse * _body.rotation;
                SetFrame(ship.Id);
                SetBody(localPos, localRot, inverse * relativeVel);
                return;
            }
        }
        else
        {
            ShipFrame ship = ShipFrame.Get(_frame);
            Vector3 localPos = _body.position;
            if (ship.ContainsLocal(localPos, _frameDistance + margin))
                return;

            ship.GetPose(tick, out Vector3 shipPos, out Quaternion shipRot);
            Vector3 worldVel = shipRot * _body.linearVelocity + ship.GetPointVelocity(localPos, tick);
            Quaternion worldRot = shipRot * _body.rotation;
            SetFrame(WorldFrame);
            SetBody(shipPos + shipRot * localPos, worldRot, worldVel);
        }
    }

    private static Vector3 Clamp(Vector3 v) => Vector3.Max(Vector3.Min(v, Vector3.one), -Vector3.one);

    private static bool IsValid(Quaternion q) => q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w > 0.5f;
    #endregion

    #region Rendering.
    private void TimeManager_OnPostTick()
    {
        _previous = _current;
        _current = TakeSnapshot();
    }

    private void PredictionManager_OnPostReconcile(uint clientTick, uint serverTick)
    {
        _current = TakeSnapshot();
    }

    private Snapshot TakeSnapshot() => new()
    {
        Frame = _frame,
        Position = _body.position,
        Rotation = _body.rotation,
    };

    private void LateUpdate()
    {
        if (_body == null || !IsClientStarted)
            return;
        GetRenderPose(out Vector3 position, out Quaternion rotation);
        transform.SetPositionAndRotation(position, rotation);
    }

    /// <summary>
    /// World pose as drawn this frame: between the last two ticks, on the ship as drawn.
    /// </summary>
    public void GetRenderPose(out Vector3 position, out Quaternion rotation)
    {
        float alpha = (float)TimeManager.GetTickPercentAsDouble();
        double renderTick = ShipClock.RenderTick;
        if (_previous.Frame == _current.Frame)
        {
            position = Vector3.Lerp(_previous.Position, _current.Position, alpha);
            rotation = Quaternion.Slerp(_previous.Rotation, _current.Rotation, alpha);
            ShipFrame.FrameToWorld(_current.Frame, renderTick, ref position, ref rotation);
        }
        else
        {
            // Crossed between ship and world this tick: blend in world space so there's no jump.
            Vector3 fromPos = _previous.Position;
            Quaternion fromRot = _previous.Rotation;
            ShipFrame.FrameToWorld(_previous.Frame, renderTick, ref fromPos, ref fromRot);
            position = _current.Position;
            rotation = _current.Rotation;
            ShipFrame.FrameToWorld(_current.Frame, renderTick, ref position, ref rotation);
            position = Vector3.Lerp(fromPos, position, alpha);
            rotation = Quaternion.Slerp(fromRot, rotation, alpha);
        }
    }

    /// <summary>
    /// Flight readout and docking guidance for the pilot's HUD.
    /// </summary>
    public string GetStatusText()
    {
        ShipFrame ship = ShipFrame.Get(_frame);
        string frame = ship == null ? "Space" : $"Ship {_frame} frame";
        string mode = _mode == FighterMode.Engine
            ? "ENGINE (Newtonian: you keep drifting, Shift brakes)"
            : $"MANEUVER (assist: stops when you let go, max {_maneuverMaxSpeed:0} m/s)";
        string head = $"{frame} | {mode} | {Velocity.magnitude:0.0} m/s";

        switch (_dock)
        {
            case FighterDock.Docked:
                return $"{head} | DOCKED\nR release clamps  F exit";
            case FighterDock.Capturing:
                float pct = Mathf.Clamp01(_captureTicks * (float)TimeManager.TickDelta / _captureDuration) * 100f;
                return $"{head} | DOCKING {pct:0}%\nR abort";
        }

        string dock = "";
        if (_frame == _carrierId)
        {
            if (_captureZone.Contains(_body.position))
            {
                string problem = GetCaptureProblem();
                dock = !_captureArmed ? "Dock: leave the zone below the hatch to re-arm"
                    : problem != null ? $"Dock: {problem}"
                    : "Dock: capturing";
            }
            else
            {
                float distance = Vector3.Distance(_body.position, _captureZone.ClosestPoint(_body.position));
                dock = $"Dock: zone below the hatch, {distance:0} m away";
            }
        }
        return $"{head}\n{dock}\nMouse aim  W/S thrust  A/D roll  Q/E strafe  Z/C yaw  Space/Ctrl up/down  Shift brake  RMB look  X engine  F exit";
    }
    #endregion
}
