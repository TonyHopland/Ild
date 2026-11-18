using System.Globalization;
using ILD.Data.Enums;

namespace ILD.Core.Services.RunHosts;

/// <summary>Why a run's pod or container ended, as its host reports it.</summary>
public enum RunTerminationKind
{
    OutOfMemory,
    Evicted,
    NodeLost,
    Deleted,
    /// <summary>The pod or container is gone, and nothing says how: what it held that was not kept is lost with it.</summary>
    PodLost,
    Other,
}

/// <summary>
/// What the run's host says ended the run's container: read before the host removes or replaces it, since the reason is gone
/// with it. <see cref="LimitBytes"/> is null when the host cannot report it.
/// </summary>
public sealed record RunTermination(
    RunTerminationKind Kind,
    long? LimitBytes = null,
    string? Detail = null,
    DateTimeOffset? EndedAt = null)
{
    /// <summary>The setting that gives the limit, named in the message so the person knows what to raise.</summary>
    private static readonly string LimitSetting = RunHostOptions.MemoryLimitSetting;

    public string Describe() => Kind switch
    {
        RunTerminationKind.OutOfMemory => OutOfMemoryText(),
        RunTerminationKind.Evicted => With("The run's container was evicted by its node", Detail),
        RunTerminationKind.NodeLost => With("The run's container was lost with its node", Detail),
        RunTerminationKind.Deleted => With("The run's container was deleted while a command was running in it", Detail),
        RunTerminationKind.PodLost => "The run's container no longer exists.",
        _ => With("The run's container ended", Detail),
    };

    /// <summary>What a command in a run whose <paramref name="noun"/> (pod or container) is gone is told, with the way on.</summary>
    public static string LostText(string noun)
        => $"The run {noun} no longer exists. Retry the node: ILD makes the {noun} again with the work it kept. If the retry fails the same way, what the run had not pushed is lost and the work item has to be started again.";

    /// <summary>The reason a node that failed this way is parked with: its own for an out-of-memory kill, the generic one for the rest.</summary>
    public string HumanFeedbackReason => Kind == RunTerminationKind.OutOfMemory ? HumanFeedbackReasons.OutOfMemory : HumanFeedbackReasons.NodeFailed;

    private string OutOfMemoryText()
        => LimitBytes is { } limit
            ? $"The run ran out of memory: its container was killed at the {FormatBytes(limit)} limit (`{LimitSetting}`)."
            : $"The run ran out of memory: its container was killed for exceeding its memory limit (`{LimitSetting}`).";

    private static string With(string text, string? detail)
        => string.IsNullOrWhiteSpace(detail) ? text + "." : $"{text}: {detail.Trim()}.";

    internal static string FormatBytes(long bytes)
    {
        const long Mi = 1024L * 1024;
        const long Gi = 1024L * Mi;
        if (bytes >= Gi) return bytes % Gi == 0 ? $"{bytes / Gi}Gi" : ((double)bytes / Gi).ToString("0.#", CultureInfo.InvariantCulture) + "Gi";
        if (bytes >= Mi) return bytes % Mi == 0 ? $"{bytes / Mi}Mi" : ((double)bytes / Mi).ToString("0.#", CultureInfo.InvariantCulture) + "Mi";
        return bytes.ToString(CultureInfo.InvariantCulture) + "B";
    }
}

/// <summary>A command could not start because the run's pod or container no longer exists. The message says so and what to do.</summary>
public sealed class RunPodLostException(Guid run, string text)
    : InvalidOperationException(text)
{
    public Guid Run { get; } = run;

    /// <summary>The lost pod behind <paramref name="failure"/>, however deep it was wrapped; null when that is not what failed.</summary>
    public static RunPodLostException? In(Exception failure)
    {
        for (var e = failure; e is not null; e = e.InnerException)
            if (e is RunPodLostException lost) return lost;
        return null;
    }
}
