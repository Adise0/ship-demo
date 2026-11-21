using FishNet.Object;
using FishNet.Object.Prediction;
using FishNet.Transporting;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
// NetworkBehaviour has its own SceneManager property (FishNet's); use Unity's explicitly.
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

public struct ShipPlayerInput : IReplicateData
{
    public Vector2 Move;
    public bool JumpPressed;
    public bool Up;
    public bool Down;
    public bool Brake;
    public bool BootsToggle;
    // Camera rotation relative to LookFrame (ship-local inside a ship, world space in EVA).
    // Kept frame-relative so moving inside a ship never depends on where the ship is.
    public Quaternion Look;
    public int LookFrame;

    // Seats: F enters/leaves the helm or a fighter.
    public bool Interact;
    // Fighter: Q/E (Roll axis) strafes, Z/C yaw, mouse aim in degrees this tick (pitch down,
    // yaw right), X engine, R release. A/D roll comes from Move.
    public float Roll;
    public float Yaw;
    public Vector2 Steer;
    public bool EngineToggle;
    public bool Release;
    // Helm: the command the seated player wants. A new sequence number marks a change.
    public HelmCommand Helm;
    public byte HelmSequence;

    private uint _tick;
    public void Dispose() { }
    public uint GetTick() => _tick;
    public void SetTick(uint value) => _tick = value;
}

public enum PlayerSeat : byte
{
    None,
    Helm,
    Fighter,
}

public struct ShipPlayerState : IReconcileData
{
    // -1 = world (EVA), otherwise the id of the ship whose physics scene the body is in.
    public int Frame;
    // All of these are in the frame's space.
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Velocity;
    public bool BootsOn;
    public bool Attached;
    public PlayerSeat Seat;
    // Ship id for the helm, fighter id for a fighter.
    public int SeatTarget;
    // The piloted fighter's state, when Seat is Fighter.
    public FighterState Fighter;

    private uint _tick;
    public void Dispose() { }
    public uint GetTick() => _tick;
    public void SetTick(uint value) => _tick = value;
}

/// <summary>
/// Predicted player. Near or inside a ship it's simulated in that ship's physics scene; elsewhere
/// in the world. Each tick it moves by one rule:
///   ship gravity here        -> walk, down = gravity
///   mag boots on a surface   -> walk on that surface, down = into the surface
///   otherwise                -> float (thrusters), feeling the ship's motion as fictitious forces
/// Seated at a ship's helm the body is held in the seat and the player's input flies the ship.
/// Seated in a fighter the body rides along and the fighter is simulated as part of this player.
/// The physics body is a separate object that moves between physics scenes; this object only renders it.
/// </summary>
public class ShipPlayer : NetworkBehaviour
{
    private const int WorldFrame = -1;

    public enum MoveMode : byte
    {
        Float,
        Walk,
        Boots,
        Seated,
    }

    [Header("Walking")]
    [SerializeField]
    private float _walkSpeed = 5f;
    [SerializeField]
    private float _sprintSpeed = 8f;
    [SerializeField]
    private float _groundAccel = 60f;
    [SerializeField]
    private float _airAccel = 10f;
    [SerializeField]
    private float _jumpSpeed = 5f;

    [Header("Floating")]
    [SerializeField]
    private float _thrustAccel = 4f;
    [SerializeField]
    private float _brakeAccel = 6f;

    [Header("Mag boots")]
    // How far beyond standing height a surface can be to lock on to it.
    [SerializeField]
    private float _bootReach = 0.25f;
    // How far beyond standing height the boots keep hold before letting go.
    [SerializeField]
    private float _bootKeep = 0.5f;
    // Acceleration pulling the feet down onto the surface.
    [SerializeField]
    private float _bootPull = 30f;
    // Fictitious acceleration (from the ship maneuvering) beyond which the boots are ripped off.
    [SerializeField]
    private float _bootHold = 8f;
    // Degrees per second the body turns to match the surface (e.g. walking over an edge).
    [SerializeField]
    private float _bootTurnSpeed = 270f;

    [Header("Body")]
    [SerializeField]
    private float _radius = 0.4f;
    [SerializeField]
    private float _height = 1.8f;
    [SerializeField]
    private float _eyeHeight = 0.7f;
    // Degrees per second the body turns toward gravity's up, or toward the look while floating.
    [SerializeField]
    private float _uprightSpeed = 180f;

    [Header("Camera")]
    [SerializeField]
    private float _mouseSensitivity = 0.1f;
    [SerializeField]
    private float _stickSensitivity = 180f;
    // Degrees per second for Q/E roll while floating.
    [SerializeField]
    private float _rollSpeed = 90f;

    [Header("Helm")]
    // Throttle change per second while W/S is held.
    [SerializeField]
    private float _throttleRate = 8f;
    // Throttle is sent in steps of this, so holding W doesn't send a new command every tick.
    [SerializeField]
    private float _throttleStep = 0.5f;

    private Rigidbody _body;
    private CapsuleCollider _capsule;
    private int _frame = WorldFrame;
    private Scene _worldScene;
    // Simulation state beyond the rigidbody; reconciled.
    private bool _bootsOn;
    private bool _attached;
    private PlayerSeat _seat;
    private int _seatTarget;
    // Derived each tick from the state above; not reconciled.
    private MoveMode _mode;

    private InputSystem_Actions _inputActions;
    // Controls not in the shared input asset: Q/E roll (strafe in a fighter), Ctrl down, B boots,
    // F interact, X engine, R release, Z/C yaw, right mouse free look.
    private InputAction _rollAction;
    private InputAction _yawAction;
    private InputAction _downAction;
    private InputAction _bootsAction;
    private InputAction _interactAction;
    private InputAction _engineAction;
    private InputAction _releaseAction;
    private InputAction _freeLookAction;
    private bool _jumpPressed;
    private bool _bootsPressed;
    private bool _interactPressed;
    private bool _enginePressed;
    private bool _releasePressed;

    // Helm controls on the owner. The throttle persists; the command goes out with every input.
    private float _helmThrottle;
    private HelmCommand _helmCommand;
    private byte _helmSequence;
    // Last helm sequence scheduled: by the server for this player, and predicted by the owner.
    private byte _serverHelmSequence;
    private byte _predictedHelmSequence;

    // Fighter controls on the owner: mouse movement since the last tick steers; Alt turns the
    // head instead, which eases back when released.
    private Vector2 _steerDegrees;
    private Quaternion _headLook = Quaternion.identity;
    private float _headYaw;
    private float _headPitch;

    // Owner camera. _lookRot is the view in _lookFrame's space. While walking (gravity or boots)
    // it's yaw/pitch around the body's up; while floating it's a free rotation.
    private Transform _camera;
    private int _lookFrame = WorldFrame;
    private Quaternion _lookRot = Quaternion.identity;
    private bool _lookUpright;
    // Upright mode: basis whose up follows the body's up, plus yaw/pitch within it.
    private Quaternion _lookBasis = Quaternion.identity;
    private float _yaw;
    private float _pitch;
    // Leftover tilt from switching to upright at an angle; eases back to identity.
    private Quaternion _lookAlign = Quaternion.identity;
    private Quaternion _lookWorld = Quaternion.identity;

    // Last two tick results, for rendering between ticks.
    private struct Snapshot
    {
        public int Frame;
        public Vector3 Position;
        public Quaternion Rotation;
        public MoveMode Mode;
        public PlayerSeat Seat;
        public int SeatTarget;
    }
    private Snapshot _previous;
    private Snapshot _current;
    private Renderer[] _renderers;

    private void Awake()
    {
        _inputActions = new InputSystem_Actions();

        _rollAction = new InputAction("Roll", InputActionType.Value);
        _rollAction.AddCompositeBinding("1DAxis")
            .With("Negative", "<Keyboard>/e")
            .With("Positive", "<Keyboard>/q");
        _downAction = new InputAction("Down", InputActionType.Button, "<Keyboard>/leftCtrl");
        _bootsAction = new InputAction("Boots", InputActionType.Button, "<Keyboard>/b");
        _interactAction = new InputAction("Interact", InputActionType.Button, "<Keyboard>/f");
        _engineAction = new InputAction("Engine", InputActionType.Button, "<Keyboard>/x");
        _releaseAction = new InputAction("Release", InputActionType.Button, "<Keyboard>/r");
        // Not Alt: on Windows a lone Alt press focuses the window's menu bar and steals input.
        _freeLookAction = new InputAction("FreeLook", InputActionType.Button, "<Mouse>/rightButton");
        _yawAction = new InputAction("Yaw", InputActionType.Value);
        _yawAction.AddCompositeBinding("1DAxis")
            .With("Negative", "<Keyboard>/z")
            .With("Positive", "<Keyboard>/c");
    }

    private void OnDestroy()
    {
        _inputActions?.Dispose();
        foreach (InputAction action in ExtraActions)
            action?.Dispose();
    }

    private InputAction[] ExtraActions => new[] { _rollAction, _yawAction, _downAction, _bootsAction, _interactAction, _engineAction, _releaseAction, _freeLookAction };

    public override void OnStartNetwork()
    {
        _worldScene = gameObject.scene;
        CreateBody();

        // Start inside ship 0, spread out by owner id so players don't overlap.
        ShipFrame ship = ShipFrame.Get(0);
        if (ship != null)
        {
            SetFrame(ship.Id);
            Vector3 spread = new((Owner.ClientId % 4 - 1.5f) * 1.5f, 0f, 0f);
            _body.position = ship.SpawnLocal + spread;
            _body.rotation = Quaternion.identity;
            _mode = MoveMode.Walk;
        }
        else
        {
            _body.position = transform.position;
        }
        _current = TakeSnapshot();
        _previous = _current;

        TimeManager.OnTick += TimeManager_OnTick;
        TimeManager.OnPostTick += TimeManager_OnPostTick;
        PredictionManager.OnPostReconcile += PredictionManager_OnPostReconcile;
    }

    public override void OnStopNetwork()
    {
        TimeManager.OnTick -= TimeManager_OnTick;
        TimeManager.OnPostTick -= TimeManager_OnPostTick;
        PredictionManager.OnPostReconcile -= PredictionManager_OnPostReconcile;

        // Leaving (e.g. disconnecting) frees the seat; a fighter parks where it is.
        LeaveSeat(placeBody: false);

        if (_body != null)
            Destroy(_body.gameObject);
    }

    public override void OnStartClient()
    {
        if (!IsOwner)
            return;

        _inputActions.Player.Enable();
        foreach (InputAction action in ExtraActions)
            action.Enable();
        if (Camera.main != null)
            _camera = Camera.main.transform;
        _lookFrame = _frame;
        // First person: don't render our own body in front of the camera.
        foreach (Renderer r in GetComponentsInChildren<Renderer>())
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    public override void OnStopClient()
    {
        _inputActions.Player.Disable();
        foreach (InputAction action in ExtraActions)
            action.Disable();
        if (IsOwner)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    private void CreateBody()
    {
        GameObject go = new($"{name}_Body");
        UnitySceneManager.MoveGameObjectToScene(go, _worldScene);
        // The new body is in the world scene, whatever frame a previous session left this in.
        _frame = WorldFrame;
        _seat = PlayerSeat.None;

        _capsule = go.AddComponent<CapsuleCollider>();
        _capsule.radius = _radius;
        _capsule.height = _height;
        // Frictionless so the player doesn't stick to walls; walking handles its own grip.
        _capsule.sharedMaterial = new PhysicsMaterial("Player")
        {
            dynamicFriction = 0f,
            staticFriction = 0f,
            frictionCombine = PhysicsMaterialCombine.Minimum,
            bounciness = 0f,
            bounceCombine = PhysicsMaterialCombine.Minimum,
        };

        _body = go.AddComponent<Rigidbody>();
        _body.useGravity = false;
        _body.freezeRotation = true;
        _body.interpolation = RigidbodyInterpolation.None;
        _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
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

    private PhysicsScene CurrentPhysicsScene =>
        _frame == WorldFrame ? Physics.defaultPhysicsScene : ShipFrame.Get(_frame).InteriorPhysics;

    #region Input.
    private void Update()
    {
        if (!IsOwner)
            return;

        if (_inputActions.Player.Jump.WasPressedThisFrame())
            _jumpPressed = true;
        if (_bootsAction.WasPressedThisFrame())
            _bootsPressed = true;
        if (_interactAction.WasPressedThisFrame())
            _interactPressed = true;
        if (_engineAction.WasPressedThisFrame())
            _enginePressed = true;
        if (_releaseAction.WasPressedThisFrame())
            _releasePressed = true;

        if (_seat == PlayerSeat.Helm)
            UpdateHelm();

        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            bool locked = Cursor.lockState == CursorLockMode.Locked;
            Cursor.lockState = locked ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = locked;
        }

        UpdateLook();
    }

    private void UpdateLook()
    {
        InputAction look = _inputActions.Player.Look;
        Vector2 delta = look.ReadValue<Vector2>();
        if (look.activeControl?.device is Mouse)
            delta *= _mouseSensitivity;
        else
            delta *= _stickSensitivity * Time.deltaTime;
        if (Cursor.lockState != CursorLockMode.Locked)
            delta = Vector2.zero;

        if (_current.Seat == PlayerSeat.Fighter)
        {
            UpdateFighterLook(delta);
            return;
        }
        _headLook = Quaternion.identity;
        _headYaw = _headPitch = 0f;
        _steerDegrees = Vector2.zero;

        // The rendered body changed frame: carry the view over through world space so it doesn't snap.
        if (_current.Frame != _lookFrame)
        {
            Quaternion world = RenderFrameRotation(_lookFrame) * _lookRot;
            _lookRot = Quaternion.Inverse(RenderFrameRotation(_current.Frame)) * world;
            _lookFrame = _current.Frame;
            _lookUpright = false;
        }

        if (_current.Mode != MoveMode.Float)
        {
            // Walking (gravity or boots): yaw/pitch around the body's up, which follows the surface.
            Vector3 bodyUp = RenderedRotationInFrame() * Vector3.up;
            if (!_lookUpright)
                EnterUprightLook(bodyUp);
            else
                _lookBasis = Quaternion.FromToRotation(_lookBasis * Vector3.up, bodyUp) * _lookBasis;

            _yaw += delta.x;
            _pitch = Mathf.Clamp(_pitch - delta.y, -89f, 89f);
            _lookAlign = Quaternion.RotateTowards(_lookAlign, Quaternion.identity, _uprightSpeed * Time.deltaTime);
            _lookRot = _lookAlign * _lookBasis * Quaternion.Euler(_pitch, _yaw, 0f);
        }
        else
        {
            // Floating: free 6-DOF look; Q/E roll (Q = counter-clockwise).
            _lookUpright = false;
            float roll = _rollAction.ReadValue<float>() * _rollSpeed * Time.deltaTime;
            _lookRot *= Quaternion.Euler(-delta.y, delta.x, roll);
        }

        _lookWorld = RenderFrameRotation(_lookFrame) * _lookRot;
    }

    /// <summary>
    /// In a fighter the mouse steers; with Alt held it turns the head instead (e.g. to look up
    /// through the canopy at the hatch), and the head eases back forward when Alt is let go.
    /// </summary>
    private void UpdateFighterLook(Vector2 delta)
    {
        Fighter fighter = Fighter.Get(_current.SeatTarget);
        if (fighter == null)
            return;

        if (_freeLookAction.IsPressed())
        {
            _headYaw = Mathf.Clamp(_headYaw + delta.x, -150f, 150f);
            _headPitch = Mathf.Clamp(_headPitch - delta.y, -85f, 85f);
            _headLook = Quaternion.Euler(_headPitch, _headYaw, 0f);
        }
        else
        {
            _steerDegrees += delta;
            _headLook = Quaternion.RotateTowards(_headLook, Quaternion.identity, 360f * Time.deltaTime);
            Vector3 euler = _headLook.eulerAngles;
            _headPitch = Mathf.DeltaAngle(0f, euler.x);
            _headYaw = Mathf.DeltaAngle(0f, euler.y);
        }

        fighter.GetRenderPose(out _, out Quaternion fighterRot);
        _lookWorld = fighterRot * _headLook;
        // Keep the walking/floating view in step, so stepping out continues from this view.
        _lookFrame = _current.Frame;
        _lookRot = Quaternion.Inverse(RenderFrameRotation(_lookFrame)) * _lookWorld;
        _lookUpright = false;
    }

    /// <summary>
    /// Helm controls: W/S move the throttle (it stays where it's left), Shift all stop,
    /// A/D yaw, Space/Ctrl pitch, Q/E roll.
    /// </summary>
    private void UpdateHelm()
    {
        ShipFrame ship = ShipFrame.Get(_seatTarget);
        if (ship == null)
            return;

        var player = _inputActions.Player;
        Vector2 move = player.Move.ReadValue<Vector2>();
        _helmThrottle = Mathf.Clamp(_helmThrottle + move.y * _throttleRate * Time.deltaTime, -ship.MaxReverseSpeed, ship.MaxSpeed);
        if (player.Sprint.IsPressed())
            _helmThrottle = 0f;

        float pitch = (_downAction.IsPressed() ? 1f : 0f) - (player.Jump.IsPressed() ? 1f : 0f);
        HelmCommand command = new()
        {
            Speed = Mathf.Round(_helmThrottle / _throttleStep) * _throttleStep,
            Turn = new Vector3(pitch, Mathf.Round(move.x), _rollAction.ReadValue<float>()),
        };
        if (!command.Equals(_helmCommand) || _helmSequence == 0)
        {
            _helmCommand = command;
            // 0 is reserved for commands the server makes itself.
            _helmSequence = (byte)(_helmSequence == byte.MaxValue ? 1 : _helmSequence + 1);
        }
    }

    /// <summary>
    /// Switches the view to upright yaw/pitch around bodyUp, keeping it exactly where it was:
    /// the difference (mostly roll) goes into _lookAlign and eases out.
    /// </summary>
    private void EnterUprightLook(Vector3 bodyUp)
    {
        Vector3 forward = _lookRot * Vector3.forward;
        // Looking straight along up or down: take the heading from the view's up (or down) instead.
        Vector3 heading = Vector3.ProjectOnPlane(forward, bodyUp);
        if (heading.sqrMagnitude < 0.0001f)
            heading = Vector3.ProjectOnPlane(_lookRot * (Vector3.Dot(forward, bodyUp) > 0f ? Vector3.down : Vector3.up), bodyUp);

        _lookBasis = Quaternion.LookRotation(heading, bodyUp);
        Vector3 relative = Quaternion.Inverse(_lookBasis) * forward;
        _yaw = 0f;
        _pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(relative.y, -1f, 1f)) * Mathf.Rad2Deg, -89f, 89f);
        _lookAlign = _lookRot * Quaternion.Inverse(_lookBasis * Quaternion.Euler(_pitch, 0f, 0f));
        _lookUpright = true;
    }

    private static Quaternion RenderFrameRotation(int frame)
    {
        ShipFrame ship = ShipFrame.Get(frame);
        if (ship == null)
            return Quaternion.identity;
        ship.GetPose(ShipClock.RenderTick, out _, out Quaternion rot);
        return rot;
    }

    // Body rotation as drawn this frame, in the current snapshot's frame space.
    private Quaternion RenderedRotationInFrame()
    {
        if (_previous.Frame != _current.Frame)
            return _current.Rotation;
        float alpha = (float)TimeManager.GetTickPercentAsDouble();
        return Quaternion.Slerp(_previous.Rotation, _current.Rotation, alpha);
    }

    private ShipPlayerInput CreateReplicateData()
    {
        if (!IsOwner)
            return default;

        var player = _inputActions.Player;
        ShipPlayerInput data = new()
        {
            Move = player.Move.ReadValue<Vector2>(),
            JumpPressed = _jumpPressed,
            Up = player.Jump.IsPressed(),
            Down = _downAction.IsPressed(),
            Brake = player.Sprint.IsPressed(),
            BootsToggle = _bootsPressed,
            Look = _lookRot,
            LookFrame = _lookFrame,
            Interact = _interactPressed,
            Roll = _rollAction.ReadValue<float>(),
            EngineToggle = _enginePressed,
            Release = _releasePressed,
        };

        Fighter fighter = _seat == PlayerSeat.Fighter ? Fighter.Get(_seatTarget) : null;
        if (fighter != null)
        {
            // Mouse aim: send what the fighter can turn this tick, and carry the rest over so a
            // quick flick still turns all the way, just at the fighter's turn rate.
            Vector2 max = fighter.MaxAimPerTick((float)TimeManager.TickDelta);
            Vector2 aim = new(Mathf.Clamp(-_steerDegrees.y, -max.x, max.x), Mathf.Clamp(_steerDegrees.x, -max.y, max.y));
            data.Steer = aim;
            data.Yaw = _yawAction.ReadValue<float>();
            _steerDegrees -= new Vector2(aim.y, -aim.x);
            _steerDegrees = Vector2.ClampMagnitude(_steerDegrees, 45f);
        }
        else
        {
            _steerDegrees = Vector2.zero;
        }

        if (_seat == PlayerSeat.Helm)
        {
            data.Helm = _helmCommand;
            data.HelmSequence = _helmSequence;
        }

        _jumpPressed = false;
        _bootsPressed = false;
        _interactPressed = false;
        _enginePressed = false;
        _releasePressed = false;
        return data;
    }
    #endregion

    private void TimeManager_OnTick()
    {
        RunInputs(CreateReplicateData());
    }

    [Replicate]
    private void RunInputs(ShipPlayerInput data, ReplicateState state = ReplicateState.Invalid, Channel channel = Channel.Unreliable)
    {
        float delta = (float)TimeManager.TickDelta;
        // Body state is currently the result of the previous tick.
        long previousTick = ShipClock.SimTick - 1;

        if (data.Interact)
            Interact(state);

        switch (_seat)
        {
            case PlayerSeat.Helm:
                SimulateHelm(data, state);
                return;
            case PlayerSeat.Fighter:
                SimulateFighter(data, previousTick, delta);
                return;
        }

        if (data.BootsToggle)
            _bootsOn = !_bootsOn;

        UpdateFrame(previousTick);

        // Default input (no data received) has no look; keep facing the same way.
        Quaternion look = IsValid(data.Look) ? LookInCurrentFrame(data, previousTick) : _body.rotation;
        if (_frame == WorldFrame)
        {
            _attached = false;
            _mode = MoveMode.Float;
            Float(data, look, Vector3.zero, delta);
        }
        else
        {
            SimulateInShip(data, look, previousTick, delta);
        }
    }

    #region Seats.
    /// <summary>
    /// F: leaves the current seat, or takes the helm or a fighter within reach. The server's
    /// occupancy decides; a client that predicted taking a seat someone else got is corrected out.
    /// </summary>
    private void Interact(ReplicateState state)
    {
        if (_seat != PlayerSeat.None)
        {
            LeaveSeat(placeBody: true);
            return;
        }

        ShipHelm helm = ShipHelm.ForShip(_frame);
        if (helm != null && helm.IsFreeFor(ObjectId)
            && Vector3.Distance(_body.position, helm.SeatLocal) <= helm.InteractDistance)
        {
            _seat = PlayerSeat.Helm;
            _seatTarget = helm.Ship.Id;
            SetSeated(true);
            if (IsServerStarted)
                helm.SetOccupant(ObjectId);
            // Pick up the throttle where the ship is, once (not again on replays).
            if (IsOwner && !state.ContainsReplayed())
                _helmThrottle = helm.Ship.CommandAt(ShipClock.SimTick).Speed;
            return;
        }

        foreach (Fighter fighter in Fighter.All)
        {
            if (fighter.Frame != _frame || !fighter.IsFreeFor(ObjectId)
                || Vector3.Distance(_body.position, fighter.Position) > fighter.BoardDistance)
                continue;

            _seat = PlayerSeat.Fighter;
            _seatTarget = fighter.Id;
            SetSeated(true);
            fighter.BeginPiloting(this);
            FollowFighter(fighter);
            return;
        }
    }

    /// <summary>
    /// Gets up from the current seat. placeBody puts the body back in the world (beside the
    /// fighter, or standing at the helm); without it (despawning) only the seat is freed.
    /// Returns false if the seat can't be left right now (a fighter mid-capture).
    /// </summary>
    private bool LeaveSeat(bool placeBody)
    {
        switch (_seat)
        {
            case PlayerSeat.Helm:
                ShipHelm helm = ShipHelm.ForShip(_seatTarget);
                if (helm != null && IsServerStarted)
                {
                    helm.SetOccupant(-1);
                    helm.ScheduleNeutral();
                }
                break;
            case PlayerSeat.Fighter:
                Fighter fighter = Fighter.Get(_seatTarget);
                if (fighter != null)
                {
                    if (placeBody && !fighter.CanExit)
                        return false;
                    if (placeBody)
                    {
                        SetSeated(false);
                        SetFrame(fighter.Frame);
                        SetBody(fighter.ExitPosition, fighter.Rotation, fighter.Velocity);
                    }
                    fighter.EndPiloting(this);
                }
                break;
            default:
                return true;
        }

        _seat = PlayerSeat.None;
        if (_body != null)
            SetSeated(false);
        return true;
    }

    /// <summary>
    /// Seated, the body is held in place by the seat: no collisions, no physics.
    /// </summary>
    private void SetSeated(bool seated)
    {
        if (seated && !_body.isKinematic)
            _body.linearVelocity = Vector3.zero;
        _body.isKinematic = seated;
        _capsule.enabled = !seated;
    }

    /// <summary>
    /// At the helm: the body stays in the seat and the input flies the ship. Changed commands are
    /// scheduled a little ahead by the server; the helm's own client predicts the same so the
    /// ship answers its controls right away.
    /// </summary>
    private void SimulateHelm(ShipPlayerInput data, ReplicateState state)
    {
        ShipHelm helm = ShipHelm.ForShip(_seatTarget);
        if (helm == null)
        {
            LeaveSeat(placeBody: false);
            return;
        }

        SetFrame(_seatTarget);
        _mode = MoveMode.Seated;
        _attached = false;
        _body.position = helm.SeatLocal;
        _body.rotation = Quaternion.identity;

        byte sequence = data.HelmSequence;
        if (sequence == 0)
            return;
        long tick = ShipClock.SimTick + helm.CommandDelayTicks;
        if (IsServerStarted)
        {
            if (state.ContainsCreated() && sequence != _serverHelmSequence)
            {
                _serverHelmSequence = sequence;
                helm.Schedule(tick, data.Helm, sequence);
            }
        }
        else if (IsOwner && !state.ContainsReplayed() && sequence != _predictedHelmSequence)
        {
            _predictedHelmSequence = sequence;
            helm.Ship.ApplyCommand(tick, data.Helm, sequence, predicted: true);
        }
    }

    /// <summary>
    /// In a fighter: the input flies it, and the body rides along in the cockpit.
    /// </summary>
    private void SimulateFighter(ShipPlayerInput data, long tick, float delta)
    {
        Fighter fighter = Fighter.Get(_seatTarget);
        if (fighter == null)
        {
            LeaveSeat(placeBody: false);
            return;
        }

        _mode = MoveMode.Seated;
        _attached = false;
        fighter.BeginPiloting(this);
        // A/D roll (A counter-clockwise), Q/E strafe (Q left), Z/C yaw, W/S forward/back.
        FighterInput input = new()
        {
            Thrust = new Vector3(-data.Roll, (data.Up ? 1f : 0f) - (data.Down ? 1f : 0f), data.Move.y),
            Yaw = data.Yaw,
            Roll = -data.Move.x,
            Aim = data.Steer,
            Brake = data.Brake,
            EngineToggle = data.EngineToggle,
            Release = data.Release,
        };
        fighter.Simulate(input, tick, delta);
        FollowFighter(fighter);
    }

    private void FollowFighter(Fighter fighter)
    {
        SetFrame(fighter.Frame);
        _body.position = fighter.Position + fighter.Rotation * fighter.CockpitPosition;
        _body.rotation = fighter.Rotation;
    }
    #endregion

    /// <summary>
    /// Look rotation expressed in the body's current frame. Usually the client looked from the
    /// same frame and no conversion is needed; only around frame transitions does it go through
    /// the ship pose.
    /// </summary>
    private Quaternion LookInCurrentFrame(ShipPlayerInput data, long tick)
    {
        if (data.LookFrame == _frame)
            return data.Look;

        Quaternion world = data.Look;
        ShipFrame from = ShipFrame.Get(data.LookFrame);
        if (from != null)
        {
            from.GetPose(tick, out _, out Quaternion fromRot);
            world = fromRot * data.Look;
        }

        ShipFrame to = ShipFrame.Get(_frame);
        if (to == null)
            return world;
        to.GetPose(tick, out _, out Quaternion toRot);
        return Quaternion.Inverse(toRot) * world;
    }

    /// <summary>
    /// Moves the body between the world and a ship when it enters or leaves the ship's zone
    /// (interior or near the hull). The margin makes entering and leaving happen at slightly
    /// different places so the body can't flicker between frames on the boundary.
    /// </summary>
    private void UpdateFrame(long tick)
    {
        const float margin = 0.3f;

        if (_frame == WorldFrame)
        {
            foreach (ShipFrame ship in ShipFrame.All)
            {
                ship.GetPose(tick, out Vector3 shipPos, out Quaternion shipRot);
                Quaternion inverse = Quaternion.Inverse(shipRot);
                Vector3 localPos = inverse * (_body.position - shipPos);
                if (!ship.IsInShipZone(localPos, -margin))
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
            if (ship.IsInShipZone(localPos, margin))
                return;

            ship.GetPose(tick, out Vector3 shipPos, out Quaternion shipRot);
            Vector3 worldVel = shipRot * _body.linearVelocity + ship.GetPointVelocity(localPos, tick);
            Quaternion worldRot = shipRot * _body.rotation;
            SetFrame(WorldFrame);
            SetBody(shipPos + shipRot * localPos, worldRot, worldVel);
            _attached = false;
        }
    }

    /// <summary>
    /// In a ship's frame: picks gravity walking, mag boots or floating for this tick.
    /// </summary>
    private void SimulateInShip(ShipPlayerInput data, Quaternion look, long tick, float delta)
    {
        ShipFrame ship = ShipFrame.Get(_frame);
        Vector3 position = _body.position;
        Vector3 velocity = _body.linearVelocity;
        Vector3 fictitious = ship.GetFictitiousAcceleration(position, velocity, tick) * (1f - ship.InertialDampening);

        Vector3 gravity = ship.GetGravity(position);
        if (gravity != Vector3.zero)
        {
            _attached = false;
            _mode = MoveMode.Walk;
            WalkWithGravity(data, look, gravity, fictitious, delta);
            return;
        }

        // Boots hold unless the ship maneuvers harder than they can take.
        float halfHeight = _height * 0.5f;
        if (_bootsOn && fictitious.magnitude <= _bootHold
            && ship.TryGetSurface(position, halfHeight + _bootKeep, out Vector3 surfaceUp, out float distance))
        {
            // Lock on when close and not moving away (so a jump doesn't instantly re-attach);
            // once locked, keep hold over a slightly larger distance.
            bool hold = _attached
                ? distance <= halfHeight + _bootKeep
                : distance <= halfHeight + _bootReach && Vector3.Dot(velocity, surfaceUp) <= 0.1f;
            if (hold)
            {
                _attached = true;
                _mode = MoveMode.Boots;
                WalkOnSurface(data, look, surfaceUp, distance, delta);
                return;
            }
        }

        _attached = false;
        _mode = MoveMode.Float;
        Float(data, look, fictitious, delta);
    }

    /// <summary>
    /// Artificial gravity: walk with down = gravity, snappy velocity control on the ground.
    /// </summary>
    private void WalkWithGravity(ShipPlayerInput data, Quaternion look, Vector3 gravity, Vector3 fictitious, float delta)
    {
        Vector3 up = -gravity.normalized;
        Vector3 forward = Heading(look, up);
        Vector3 right = Vector3.Cross(up, forward);

        // The body eases its up toward gravity's up rather than snapping (e.g. coming in tilted).
        Vector3 bodyUp = Vector3.RotateTowards(_body.rotation * Vector3.up, up, _uprightSpeed * Mathf.Deg2Rad * delta, 0f);
        Vector3 facing = Vector3.ProjectOnPlane(forward, bodyUp);
        _body.rotation = Quaternion.LookRotation(facing.sqrMagnitude > 0.0001f ? facing : forward, bodyUp);

        // Ship maneuvers push you too; on the ground your grip mostly cancels them.
        Vector3 velocity = _body.linearVelocity + fictitious * delta;
        bool grounded = Vector3.Dot(velocity, up) <= 0.1f && IsGrounded(-up);

        float speed = data.Brake ? _sprintSpeed : _walkSpeed;
        Vector2 move = Vector2.ClampMagnitude(data.Move, 1f);
        Vector3 wish = (forward * move.y + right * move.x) * speed;
        Vector3 horizontal = Vector3.ProjectOnPlane(velocity, up);
        horizontal = Vector3.MoveTowards(horizontal, wish, (grounded ? _groundAccel : _airAccel) * delta);

        float vertical = Vector3.Dot(velocity, up);
        if (grounded)
            vertical = data.JumpPressed ? _jumpSpeed : Mathf.Min(vertical, 0f);
        vertical -= gravity.magnitude * delta;

        _body.linearVelocity = horizontal + up * vertical;
    }

    /// <summary>
    /// Mag boots: walk along whatever surface is underfoot, down = into the surface. The surface
    /// normal is blended from nearby hull, so walking over an edge rolls you onto the next face.
    /// Jumping pushes off and lets go.
    /// </summary>
    private void WalkOnSurface(ShipPlayerInput data, Quaternion look, Vector3 surfaceUp, float distance, float delta)
    {
        Vector3 bodyUp = Vector3.RotateTowards(_body.rotation * Vector3.up, surfaceUp, _bootTurnSpeed * Mathf.Deg2Rad * delta, 0f);
        Vector3 forward = Heading(look, bodyUp);
        Vector3 right = Vector3.Cross(bodyUp, forward);
        _body.rotation = Quaternion.LookRotation(forward, bodyUp);

        Vector3 velocity = _body.linearVelocity;
        float speed = data.Brake ? _sprintSpeed : _walkSpeed;
        Vector2 move = Vector2.ClampMagnitude(data.Move, 1f);
        Vector3 wish = Vector3.ProjectOnPlane((forward * move.y + right * move.x) * speed, surfaceUp);
        Vector3 tangent = Vector3.ProjectOnPlane(velocity, surfaceUp);
        tangent = Vector3.MoveTowards(tangent, wish, _groundAccel * delta);

        // Pull toward standing height above the surface, without bouncing. Standing height is how
        // far the capsule actually reaches toward the surface at its current tilt, so while the
        // body is still turning (e.g. rolling over an edge) it hovers instead of grinding into
        // the hull; contact resolution doesn't replay exactly, so avoiding it keeps prediction clean.
        float reach = _radius + (_height * 0.5f - _radius) * Mathf.Abs(Vector3.Dot(bodyUp, surfaceUp));
        float normalSpeed = Vector3.Dot(velocity, surfaceUp);
        float targetSpeed = Mathf.Clamp((reach + 0.02f - distance) * 10f, -4f, 4f);
        normalSpeed = Mathf.MoveTowards(normalSpeed, targetSpeed, _bootPull * delta);

        if (data.JumpPressed)
        {
            normalSpeed = _jumpSpeed;
            _attached = false;
        }

        _body.linearVelocity = tangent + surfaceUp * normalSpeed;
    }

    /// <summary>
    /// No gravity and no boots hold: thrusters along the look direction, momentum is kept, and
    /// inside a ship's frame the ship's maneuvers are felt as fictitious acceleration.
    /// </summary>
    private void Float(ShipPlayerInput data, Quaternion look, Vector3 fictitious, float delta)
    {
        // Turn toward the look direction; thrust below uses the look directly.
        _body.rotation = Quaternion.RotateTowards(_body.rotation, look, _uprightSpeed * delta);

        Vector3 velocity = _body.linearVelocity + fictitious * delta;
        if (data.Brake)
        {
            // Brake toward rest in the current frame (i.e. matching the ship's motion when near it).
            velocity = Vector3.MoveTowards(velocity, Vector3.zero, _brakeAccel * delta);
        }
        else
        {
            float vertical = (data.Up ? 1f : 0f) - (data.Down ? 1f : 0f);
            Vector3 thrust = look * new Vector3(data.Move.x, vertical, data.Move.y);
            velocity += Vector3.ClampMagnitude(thrust, 1f) * (_thrustAccel * delta);
        }
        _body.linearVelocity = velocity;
    }

    /// <summary>
    /// Look direction flattened onto the plane of up; falls back to the view's up when looking
    /// straight along it.
    /// </summary>
    private static Vector3 Heading(Quaternion look, Vector3 up)
    {
        Vector3 forward = Vector3.ProjectOnPlane(look * Vector3.forward, up);
        if (forward.sqrMagnitude < 0.0001f)
            forward = Vector3.ProjectOnPlane(look * Vector3.up, up);
        return forward.normalized;
    }

    private bool IsGrounded(Vector3 down)
    {
        float castRadius = _radius * 0.95f;
        float distance = _height * 0.5f - castRadius + 0.08f;
        // Sweeps ignore colliders they start inside, so our own capsule isn't hit.
        return CurrentPhysicsScene.SphereCast(_body.position, castRadius, down, out _,
            distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
    }

    private static bool IsValid(Quaternion q) => q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w > 0.5f;

    private void SetBody(Vector3 position, Quaternion rotation, Vector3 velocity)
    {
        _body.position = position;
        _body.rotation = rotation;
        if (_body.isKinematic)
            return;
        _body.linearVelocity = velocity;
        _body.angularVelocity = Vector3.zero;
    }

    private void TimeManager_OnPostTick()
    {
        _previous = _current;
        _current = TakeSnapshot();
        CreateReconcile();
    }

    public override void CreateReconcile()
    {
        ShipPlayerState state = new()
        {
            Frame = _frame,
            Position = _body.position,
            Rotation = _body.rotation,
            Velocity = _body.linearVelocity,
            BootsOn = _bootsOn,
            Attached = _attached,
            Seat = _seat,
            SeatTarget = _seatTarget,
        };
        Fighter fighter = _seat == PlayerSeat.Fighter ? Fighter.Get(_seatTarget) : null;
        if (fighter != null)
            state.Fighter = fighter.GetState();
        ReconcileState(state);
    }

    [Reconcile]
    private void ReconcileState(ShipPlayerState data, Channel channel = Channel.Unreliable)
    {
        // Out of a fighter (or into another): it parks until its pilot's state says otherwise.
        if (_seat == PlayerSeat.Fighter && (data.Seat != PlayerSeat.Fighter || data.SeatTarget != _seatTarget))
            Fighter.Get(_seatTarget)?.EndPiloting(this);

        _seat = data.Seat;
        _seatTarget = data.SeatTarget;
        SetSeated(_seat != PlayerSeat.None);
        SetFrame(data.Frame);
        SetBody(data.Position, IsValid(data.Rotation) ? data.Rotation : Quaternion.identity, data.Velocity);
        _bootsOn = data.BootsOn;
        _attached = data.Attached;

        if (_seat == PlayerSeat.Fighter)
        {
            Fighter fighter = Fighter.Get(_seatTarget);
            if (fighter != null)
            {
                fighter.BeginPiloting(this);
                fighter.SetState(data.Fighter);
            }
        }
    }

    private void PredictionManager_OnPostReconcile(uint clientTick, uint serverTick)
    {
        // Replays may have corrected the latest tick; render toward the corrected result.
        _current = TakeSnapshot();
    }

    private Snapshot TakeSnapshot() => new()
    {
        Frame = _frame,
        Position = _body.position,
        Rotation = _body.rotation,
        Mode = _mode,
        Seat = _seat,
        SeatTarget = _seatTarget,
    };

    #region Rendering.
    private void LateUpdate()
    {
        if (_body == null || !IsClientStarted)
            return;

        float alpha = (float)TimeManager.GetTickPercentAsDouble();
        double renderTick = ShipClock.RenderTick;
        Vector3 position;
        Quaternion rotation;
        if (_previous.Frame == _current.Frame)
        {
            // Same frame: interpolate in that frame, then place it on the ship as drawn this frame.
            Snapshot blended = new()
            {
                Frame = _current.Frame,
                Position = Vector3.Lerp(_previous.Position, _current.Position, alpha),
                Rotation = Quaternion.Slerp(_previous.Rotation, _current.Rotation, alpha),
            };
            ToWorld(blended, renderTick, out position, out rotation);
        }
        else
        {
            // Crossed between ship and world this tick: blend in world space so there's no jump.
            ToWorld(_previous, renderTick, out Vector3 fromPos, out Quaternion fromRot);
            ToWorld(_current, renderTick, out Vector3 toPos, out Quaternion toRot);
            position = Vector3.Lerp(fromPos, toPos, alpha);
            rotation = Quaternion.Slerp(fromRot, toRot, alpha);
        }

        transform.SetPositionAndRotation(position, rotation);

        // In a fighter the body is out of sight in the cockpit.
        bool visible = _current.Seat != PlayerSeat.Fighter;
        if (_renderers == null)
            _renderers = GetComponentsInChildren<Renderer>();
        foreach (Renderer r in _renderers)
            r.enabled = visible;

        if (!IsOwner || _camera == null)
            return;
        Fighter fighter = _current.Seat == PlayerSeat.Fighter ? Fighter.Get(_current.SeatTarget) : null;
        if (fighter != null)
        {
            fighter.GetRenderPose(out Vector3 fighterPos, out Quaternion fighterRot);
            _camera.SetPositionAndRotation(fighterPos + fighterRot * fighter.CockpitPosition, _lookWorld);
        }
        else
        {
            _camera.SetPositionAndRotation(position + rotation * (Vector3.up * _eyeHeight), _lookWorld);
        }
    }

    /// <summary>
    /// Converts a snapshot to world space, placing ship-local results on the ship as drawn at renderTick.
    /// </summary>
    private static void ToWorld(Snapshot snapshot, double renderTick, out Vector3 position, out Quaternion rotation)
    {
        position = snapshot.Position;
        rotation = snapshot.Rotation;
        ShipFrame.FrameToWorld(snapshot.Frame, renderTick, ref position, ref rotation);
    }

    private void OnGUI()
    {
        if (!IsOwner || _body == null)
            return;

        string text;
        switch (_current.Seat)
        {
            case PlayerSeat.Helm:
                ShipFrame ship = ShipFrame.Get(_current.SeatTarget);
                float speed = ship == null ? 0f : ship.GetState(ShipClock.SimTick).Velocity.magnitude;
                text = $"HELM | Throttle {_helmThrottle:0.0} m/s | Speed {speed:0.0} m/s\n" +
                    "W/S throttle  Shift all stop  A/D yaw  Space/Ctrl pitch  Q/E roll  Mouse look  F leave";
                break;
            case PlayerSeat.Fighter:
                Fighter fighter = Fighter.Get(_current.SeatTarget);
                text = fighter == null ? "" : fighter.GetStatusText();
                break;
            default:
                string frame = _current.Frame == WorldFrame ? "Space" : $"Ship {_current.Frame}";
                string boots = _bootsOn ? (_attached ? "ON (attached)" : "ON") : "off";
                text = $"{frame} | {_current.Mode} | Boots {boots}\n" +
                    "WASD move  Space jump/up  Ctrl down  Q/E roll  Shift sprint/brake  B boots  F use helm/fighter  Esc cursor";
                break;
        }
        GUI.Label(new Rect(10f, Screen.height - 65f, 1000f, 60f), text);
    }
    #endregion
}
