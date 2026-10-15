using FishNet.Managing;
using FishNet.Managing.Predicting;
using FishNet.Managing.Timing;

/// <summary>
/// Gives every peer the same tick number for the moment being simulated, so things driven
/// purely by tick (like ship paths) line up between client prediction, replays and the server.
/// </summary>
public static class ShipClock
{
    private static NetworkManager _networkManager;
    // NetworkManager.PredictionManager is internal; it lives on the same GameObject.
    private static PredictionManager _predictionManager;
    // Server tick minus client local tick, learned from the latest reconcile.
    private static long _offset;
    private static bool _hasOffset;

    public static void Initialize(NetworkManager networkManager)
    {
        if (_networkManager == networkManager)
            return;

        if (_predictionManager != null)
            _predictionManager.OnPreReconcile -= PredictionManager_OnPreReconcile;

        _networkManager = networkManager;
        _predictionManager = networkManager.GetComponent<PredictionManager>();
        _hasOffset = false;
        _predictionManager.OnPreReconcile += PredictionManager_OnPreReconcile;
    }

    private static void PredictionManager_OnPreReconcile(uint clientTick, uint serverTick)
    {
        _offset = (long)serverTick - clientTick;
        _hasOffset = true;
    }

    public static double TickDelta => _networkManager.TimeManager.TickDelta;

    private static long Offset
    {
        get
        {
            if (_networkManager.IsServerStarted)
                return 0;
            if (_hasOffset)
                return _offset;

            TimeManager tm = _networkManager.TimeManager;
            return (long)tm.Tick - tm.LocalTick;
        }
    }

    /// <summary>
    /// Local tick currently being simulated: the replayed tick during a replay,
    /// the reconciled tick while applying a reconcile, otherwise the current tick.
    /// </summary>
    private static uint LocalSimTick
    {
        get
        {
            PredictionManager pm = _predictionManager;
            if (pm.IsReconciling)
                return pm.ClientReplayTick != TimeManager.UNSET_TICK ? pm.ClientReplayTick : pm.ClientStateTick;

            return _networkManager.TimeManager.LocalTick;
        }
    }

    /// <summary>
    /// Server-aligned tick being simulated right now. State after this tick's physics step
    /// corresponds to this tick.
    /// </summary>
    public static long SimTick => LocalSimTick + Offset;

    /// <summary>
    /// Continuous server-aligned time (in ticks) to render at. Interpolates between the two
    /// most recently completed ticks.
    /// </summary>
    public static double RenderTick
    {
        get
        {
            TimeManager tm = _networkManager.TimeManager;
            return (double)tm.LocalTick - 2d + tm.GetTickPercentAsDouble() + Offset;
        }
    }
}
