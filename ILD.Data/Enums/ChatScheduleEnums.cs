namespace ILD.Data.Enums;

/// <summary>Which repositories a scheduled chat may use.</summary>
public enum ChatScheduleRepositoryScope
{
    /// <summary>Every repository, including ones added after the schedule was saved.</summary>
    All = 0,

    /// <summary>Only the repositories the schedule names.</summary>
    Selected = 1,

    /// <summary>No repository at all.</summary>
    None = 2,
}

/// <summary>What made a schedule fire.</summary>
public enum ChatScheduleTrigger
{
    /// <summary>Its cron came due.</summary>
    Cron = 0,

    /// <summary>Its owner pressed Run now.</summary>
    RunNow = 1,
}

/// <summary>How a schedule's firing went.</summary>
public enum ChatScheduleFiringOutcome
{
    /// <summary>Its chat turn is still running.</summary>
    Running = 0,

    /// <summary>Its turn finished and the agent succeeded.</summary>
    Completed = 1,

    /// <summary>It could not start a turn, or its turn failed.</summary>
    Failed = 2,

    /// <summary>Its turn was stopped from the chat, or replaced by a message the owner sent.</summary>
    Stopped = 3,

    /// <summary>No turn was started; the reason says why.</summary>
    Skipped = 4,
}
