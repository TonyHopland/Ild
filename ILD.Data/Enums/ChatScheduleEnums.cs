namespace ILD.Data.Enums;

/// <summary>Which repositories a scheduled chat may clone.</summary>
public enum ChatScheduleRepositoryScope
{
    /// <summary>Every repository, including ones added later.</summary>
    All = 0,

    /// <summary>Only the repositories the schedule lists.</summary>
    Selected = 1,

    /// <summary>No repository.</summary>
    None = 2,
}

/// <summary>What started a schedule's firing.</summary>
public enum ChatScheduleTrigger
{
    /// <summary>Its cron came due.</summary>
    Schedule = 0,

    /// <summary>Its owner pressed Run now.</summary>
    RunNow = 1,
}

/// <summary>How a schedule's firing went.</summary>
public enum ChatScheduleFiringOutcome
{
    /// <summary>Its turn is under way.</summary>
    Running = 0,

    /// <summary>Its turn succeeded.</summary>
    Completed = 1,

    /// <summary>Its turn failed, or no turn could be started.</summary>
    Failed = 2,

    /// <summary>Its turn was stopped, or a newer message replaced it.</summary>
    Stopped = 3,

    /// <summary>No turn was started, for the reason recorded with it.</summary>
    Skipped = 4,
}
