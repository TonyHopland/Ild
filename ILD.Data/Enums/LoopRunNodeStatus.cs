namespace ILD.Data.Enums;

public enum LoopRunNodeStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Skipped = 4,
    WaitingHuman = 5,
    // Previous attempt was interrupted (process restart, throttle cancel)
    // before reaching a terminal outcome.
    Interrupted = 6,
}
