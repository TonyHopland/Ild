using System.Text;
using ILD.Core.Services.RunHosts;

namespace ILD.Core.Services.Implementations;

/// <summary>What became of one process the agent left running, as the wake-up message tells it.</summary>
/// <param name="StoppedByIld">ILD stopped it at the timeout, so it was killed by a signal (143 for SIGTERM, 137 for SIGKILL).</param>
public sealed record ChildOutcome(RunChildProcess Process, bool StoppedByIld, string? OutputTail);

/// <summary>The message that wakes an agent whose children ended (or were stopped at the timeout): short, with how each ended and the tail of its output.</summary>
public static class ChildWaitMessages
{
    /// <summary>The most of one process's output the message carries.</summary>
    public const int TailBytes = 4000;
    private const int TailLines = 40;

    public static string Build(IReadOnlyList<ChildOutcome> outcomes, TimeSpan waited, bool timedOut)
    {
        var message = new StringBuilder();
        message.AppendLine(timedOut
            ? $"ILD woke you: the processes you left running when your last turn ended had not finished after {Minutes(waited)} minute(s), so ILD stopped them."
            : $"ILD woke you: the processes you left running when your last turn ended have finished (after {Minutes(waited)} minute(s)).");
        foreach (var outcome in outcomes)
        {
            message.AppendLine();
            message.Append("- pid ").Append(outcome.Process.Pid).Append(": `").Append(outcome.Process.CommandLine).AppendLine("`");
            message.AppendLine(outcome.StoppedByIld
                ? "  Stopped by ILD, so it was killed by a signal (exit code 143 for SIGTERM, 137 if it had to be killed with SIGKILL)."
                : "  Finished. Its exit code is not available to ILD: it was started by you, not by ILD, so check its output or re-run it.");
            if (!string.IsNullOrWhiteSpace(outcome.OutputTail))
            {
                message.AppendLine("  Output (end):");
                message.AppendLine("  ```");
                foreach (var line in LastLines(outcome.OutputTail))
                    message.Append("  ").AppendLine(line);
                message.AppendLine("  ```");
            }
        }
        message.AppendLine();
        message.Append("Carry on from where you left off, for example by reporting what the processes found.");
        return message.ToString();
    }

    private static int Minutes(TimeSpan waited) => Math.Max(0, (int)Math.Round(waited.TotalMinutes));

    private static IEnumerable<string> LastLines(string text)
        => text.TrimEnd().Split('\n').Select(line => line.TrimEnd('\r')).TakeLast(TailLines);
}
