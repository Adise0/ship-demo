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

    private uint _tick;
    public void Dispose() { }
    public uint GetTick() => _tick;
    public void SetTick(uint value) => _tick = value;
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

    private Rigidbody _body;
    private int _frame = WorldFrame;
    private Scene _worldScene;
    // Simulation state beyond the rigidbody; reconciled.
    private bool _bootsOn;
    private bool _attached;
    // Derived each tick from the state above; not reconciled.
    private MoveMode _mode;

    private InputSystem_Actions _inputActions;
    // Controls not in the shared input asset: Q/E roll, Ctrl down, B boots.
    private InputAction _rollAction;
    private InputAction _downAction;
    private InputAction _bootsAction;
    private bool _jumpPressed;
    private bool _bootsPressed;

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
    }
    private Snapshot _previous;
    private Snapshot _current;

    private void Awake()
    {
        _inputActions = new InputSystem_Actions();

        _rollAction = new InputAction("Roll", InputActionType.Value);
        _rollAction.AddCompositeBinding("1DAxis")
            .With("Negative", "<Keyboard>/e")
            .With("Positive", "<Keyboard>/q");
        _downAction = new InputAction("Down", InputActionType.Button, "<Keyboard>/leftCtrl");
        _bootsAction = new InputAction("Boots", InputActionType.Button, "<Keyboard>/b");
    }

    private void OnDestroy()
    {
        _inputActions?.Dispose();
        _rollAction?.Dispose();
        _downAction?.Dispose();
        _bootsAction?.Dispose();
    }

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

        if (_body != null)
            Destroy(_body.gameObject);
    }

    public override void OnStartClient()
    {
        if (!IsOwner)
            return;

        _inputActions.Player.Enable();
        _rollAction.Enable();
        _downAction.Enable();
        _bootsAction.Enable();
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
        _rollAction.Disable();
        _downAction.Disable();
        _bootsAction.Disable();
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

        CapsuleCollider capsule = go.AddComponent<CapsuleCollider>();
        capsule.radius = _radius;
        capsule.height = _height;
        // Frictionless so the player doesn't stick to walls; walking handles its own grip.
        capsule.sharedMaterial = new PhysicsMaterial("Player")
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
        };
        _jumpPressed = false;
        _bootsPressed = false;
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
        };
        ReconcileState(state);
    }

    [Reconcile]
    private void ReconcileState(ShipPlayerState data, Channel channel = Channel.Unreliable)
    {
        SetFrame(data.Frame);
        SetBody(data.Position, IsValid(data.Rotation) ? data.Rotation : Quaternion.identity, data.Velocity);
        _bootsOn = data.BootsOn;
        _attached = data.Attached;
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

        if (IsOwner && _camera != null)
            _camera.SetPositionAndRotation(position + rotation * (Vector3.up * _eyeHeight), _lookWorld);
    }

    /// <summary>
    /// Converts a snapshot to world space, placing ship-local results on the ship as drawn at renderTick.
    /// </summary>
    private static void ToWorld(Snapshot snapshot, double renderTick, out Vector3 position, out Quaternion rotation)
    {
        position = snapshot.Position;
        rotation = snapshot.Rotation;
        ShipFrame ship = ShipFrame.Get(snapshot.Frame);
        if (ship == null)
            return;

        ship.GetPose(renderTick, out Vector3 shipPos, out Quaternion shipRot);
        position = shipPos + shipRot * position;
        rotation = shipRot * rotation;
    }

    private void OnGUI()
    {
        if (!IsOwner || _body == null)
            return;

        string frame = _current.Frame == WorldFrame ? "Space" : $"Ship {_current.Frame}";
        string boots = _bootsOn ? (_attached ? "ON (attached)" : "ON") : "off";
        GUI.Label(new Rect(10f, Screen.height - 50f, 900f, 45f),
            $"{frame} | {_current.Mode} | Boots {boots}\n" +
            "WASD move  Space jump/up  Ctrl down  Q/E roll  Shift sprint/brake  B boots  Esc cursor");
    }
    #endregion
}
