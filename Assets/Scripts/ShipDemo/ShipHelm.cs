using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

/// <summary>
/// The helm seat of a ship. The seated player's commands are scheduled by the server a little
/// in the future and sent to every peer, so all of them fly the ship through the same timeline.
/// Keyframes of the authoritative motion go out regularly to correct drift and late joiners.
/// Lives on its own object because FishNet keeps scene network objects disabled on clients until
/// they spawn, and the ship's physics scene must exist before that.
/// </summary>
public class ShipHelm : NetworkBehaviour
{
    private static readonly Dictionary<int, ShipHelm> _helms = new();
    public static ShipHelm ForShip(int shipId) => _helms.TryGetValue(shipId, out ShipHelm helm) ? helm : null;

    [SerializeField]
    private ShipFrame _ship;
    // Where the seated player's body sits, ship-local, facing ship forward.
    [SerializeField]
    private Vector3 _seatLocal = new(0f, 6.4f, 13f);
    [SerializeField]
    private float _interactDistance = 2f;
    // How far ahead commands are scheduled. Covers the trip to the other peers so they rarely
    // learn of a command after its tick; capital ships are slow enough that it isn't felt.
    [SerializeField]
    private float _commandDelay = 0.25f;
    [SerializeField]
    private float _keyframeInterval = 1f;

    // ObjectId of the seated player, or -1.
    private readonly SyncVar<int> _occupant = new(-1);

    private long _nextKeyframeTick;

    public ShipFrame Ship => _ship;
    public Vector3 SeatLocal => _seatLocal;
    public float InteractDistance => _interactDistance;
    public int CommandDelayTicks => Mathf.CeilToInt(_commandDelay / (float)TimeManager.TickDelta);

    public bool IsFreeFor(int playerObjectId) => _occupant.Value == -1 || _occupant.Value == playerObjectId;

    public override void OnStartNetwork()
    {
        _helms[_ship.Id] = this;
    }

    public override void OnStopNetwork()
    {
        if (ForShip(_ship.Id) == this)
            _helms.Remove(_ship.Id);
    }

    public override void OnStartServer()
    {
        TimeManager.OnTick += TimeManager_OnTick;
    }

    public override void OnStopServer()
    {
        TimeManager.OnTick -= TimeManager_OnTick;
    }

    public override void OnSpawnServer(NetworkConnection connection)
    {
        long tick = ShipClock.SimTick;
        TargetKeyframe(connection, tick, _ship.GetState(tick), _ship.CommandAt(tick));
    }

    private void TimeManager_OnTick()
    {
        long tick = ShipClock.SimTick;
        if (tick < _nextKeyframeTick)
            return;
        _nextKeyframeTick = tick + Mathf.Max(1, Mathf.RoundToInt(_keyframeInterval / (float)TimeManager.TickDelta));
        ObserversKeyframe(tick, _ship.GetState(tick), _ship.CommandAt(tick));
    }

    [Server]
    public void SetOccupant(int playerObjectId)
    {
        _occupant.Value = playerObjectId;
    }

    /// <summary>
    /// Schedules a command from the seated player. requestedTick is where the helm client put it;
    /// it can't go in the server's past.
    /// </summary>
    [Server]
    public void Schedule(long requestedTick, HelmCommand command, byte sequence)
    {
        long tick = System.Math.Max(requestedTick, ShipClock.SimTick + 1);
        _ship.ApplyCommand(tick, command, sequence, predicted: false);
        ObserversCommand(tick, command, sequence);
    }

    /// <summary>
    /// Stops turning, keeping the current speed. Used when the helm is left.
    /// </summary>
    [Server]
    public void ScheduleNeutral()
    {
        long tick = ShipClock.SimTick + 1;
        HelmCommand command = _ship.CommandAt(tick);
        command.Turn = Vector3.zero;
        Schedule(tick, command, 0);
    }

    // The server (and a host's client) already applied these to the shared ShipFrame.
    [ObserversRpc(ExcludeServer = true)]
    private void ObserversCommand(long tick, HelmCommand command, byte sequence)
    {
        _ship.ApplyCommand(tick, command, sequence, predicted: false);
    }

    [ObserversRpc(ExcludeServer = true)]
    private void ObserversKeyframe(long tick, ShipMotionState state, HelmCommand command)
    {
        _ship.ApplyKeyframe(tick, state, command);
    }

    [TargetRpc]
    private void TargetKeyframe(NetworkConnection connection, long tick, ShipMotionState state, HelmCommand command)
    {
        if (IsServerStarted)
            return;
        _ship.ApplyKeyframe(tick, state, command);
    }
}
