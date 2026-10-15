using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Prediction;
using FishNet.Transporting;
using GameKit.Dependencies.Utilities;
using UnityEngine;
using UnityEngine.InputSystem;


public struct MovementInput : IReplicateData
{
    public Vector2 dir;
    public bool jump;


    private uint _tick;
    public void Dispose() { }
    public uint GetTick() => _tick;
    public void SetTick(uint value) => _tick = value;
}

public struct MovementState : IReconcileData
{

    public PredictionRigidbody PredictionRigidbody;

    private uint _tick;
    public void Dispose() { }
    public uint GetTick() => _tick;
    public void SetTick(uint value) => _tick = value;
}

public class PlayerMovement : NetworkBehaviour
{
    // How much force to add to the rigidbody for jumps.
    [SerializeField]
    private float _jumpForce = 8f;
    // How much force to add to the rigidbody for normal movements.
    [SerializeField]
    private float _moveForce = 15f;
    // PredictionRigidbody is set within OnStart/StopNetwork to use our
    // caching system. You could simply initialize a new instance in the field
    // but for increased performance using the cache is demonstrated.
    public PredictionRigidbody PredictionRigidbody;
    // True if to jump next replicate.
    private bool _jump;
    // Input actions; only enabled for the owner.
    private InputSystem_Actions _inputActions;
    private InputAction _moveAction;
    private InputAction _jumpAction;

    private void Awake()
    {
        PredictionRigidbody = ObjectCaches<PredictionRigidbody>.Retrieve();
        PredictionRigidbody.Initialize(GetComponent<Rigidbody>());

        _inputActions = new InputSystem_Actions();
        _moveAction = _inputActions.Player.Move;
        _jumpAction = _inputActions.Player.Jump;
    }
    private void OnDestroy()
    {
        ObjectCaches<PredictionRigidbody>.StoreAndDefault(ref PredictionRigidbody);
        _inputActions?.Dispose();
    }

    public override void OnStartClient()
    {
        if (base.IsOwner)
            _inputActions.Player.Enable();
    }

    public override void OnStopClient()
    {
        _inputActions.Player.Disable();
    }

    public override void OnOwnershipClient(NetworkConnection prevOwner)
    {
        if (base.IsOwner)
            _inputActions.Player.Enable();
        else
            _inputActions.Player.Disable();
    }
    public override void OnStartNetwork()
    {
        TimeManager.OnTick += TimeManager_OnTick;
        TimeManager.OnPostTick += TimeManager_OnPostTick;
    }

    public override void OnStopNetwork()
    {
        TimeManager.OnTick -= TimeManager_OnTick;
        TimeManager.OnPostTick -= TimeManager_OnPostTick;
    }

    private void Update()
    {
        if (base.IsOwner)
        {
            if (_jumpAction.WasPressedThisFrame())
                _jump = true;
        }
    }

    private void TimeManager_OnTick()
    {
        RunInputs(CreateReplicateData());
    }

    private MovementInput CreateReplicateData()
    {
        if (!base.IsOwner)
            return default;

        // Build the replicate data with all inputs which affect the prediction.
        MovementInput md = new MovementInput
        {
            dir = _moveAction.ReadValue<Vector2>(),
            jump = _jump,
        };
        _jump = false;

        return md;
    }

    [Replicate]
    private void RunInputs(MovementInput data, ReplicateState state = ReplicateState.Invalid, Channel channel = Channel.Unreliable)
    {
        /* ReplicateState is set based on if the data is new, being replayed, ect.
        * Visit the ReplicationState enum for more information on what each value
        * indicates. At the end of this guide a more advanced use of state will
        * be demonstrated. */

        // Be sure to always apply and set velocities using PredictionRigidbody
        // and never on the rigidbody itself; this includes if also accessing from
        // another script.
        Vector3 forces = new Vector3(data.dir.x, 0f, data.dir.y) * _moveForce;
        PredictionRigidbody.AddForce(forces);

        if (data.jump)
        {
            Vector3 jmpFrc = new Vector3(0f, _jumpForce, 0f);
            PredictionRigidbody.AddForce(jmpFrc, ForceMode.Impulse);
        }

        // Simulate the added forces.
        // Typically you call this at the end of your replicate. Calling
        // Simulate is ultimately telling the PredictionRigidbody to iterate
        // the forces we added above.
        PredictionRigidbody.Simulate();
    }

    private void TimeManager_OnPostTick()
    {
        CreateReconcile();
    }

    // Create the reconcile data here and call your reconcile method.
    public override void CreateReconcile()
    {
        // We must send back the state of the rigidbody. Using your
        // PredictionRigidbody field in the reconcile data is an easy
        // way to accomplish this. More advanced states may require other
        // values to be sent; this will be covered later on.
        MovementState rd = new MovementState
        {
            PredictionRigidbody = PredictionRigidbody
        };
        // Like with the replicate you could specify a channel here, though
        // it's unlikely you ever would with a reconcile.
        ReconcileState(rd);
    }

    [Reconcile]
    private void ReconcileState(MovementState data, Channel channel = Channel.Unreliable)
    {
        // Call reconcile on your PredictionRigidbody field passing in
        // values from data.
        PredictionRigidbody.Reconcile(data.PredictionRigidbody);
    }

}
