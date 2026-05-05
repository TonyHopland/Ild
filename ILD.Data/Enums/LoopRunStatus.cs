namespace ILD.Data.Enums;

public enum LoopRunStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3,
    /// <summary>
    /// The run is parked at a node awaiting an external event (Human input,
    /// PR webhook, etc.). The run-level status is the source of truth — the
    /// engine no longer infers "waiting" from node state.
    /// </summary>
    WaitingHuman = 4,
}
