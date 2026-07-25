using ILD.Core.Services.Remote;
using Microsoft.Extensions.Logging;

namespace ILD.Tests;

/// <summary>
/// Being at the concurrency cap with Ready work waiting is the signal WI-165
/// added — without it a board held up by a leaked slot looks exactly like an
/// idle one. It is also a state that persists across passes, at 5s intervals
/// once anything is parked at a human gate, so what an operator sees at
/// Information has to be the transitions and not the state.
/// </summary>
public class CapStallReporterTests
{
    [Fact]
    public void Announces_the_stall_once_and_traces_the_passes_after_it()
    {
        var log = new RecordingLogger();
        var reporter = new CapStallReporter(log);

        for (var pass = 0; pass < 4; pass++)
            reporter.Report(Blocked("wi-1", "wi-2"), maxConcurrent: 2);

        var announcement = Assert.Single(log.At(LogLevel.Information));
        Assert.Contains("wi-1", announcement, StringComparison.Ordinal);
        Assert.Contains("wi-2", announcement, StringComparison.Ordinal);
        Assert.Contains("cap", announcement, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, log.At(LogLevel.Debug).Count);
    }

    [Fact]
    public void Announces_again_when_the_holders_change()
    {
        // The board moved and is still stuck — the most interesting moment
        // there is, and one a once-only line would hide.
        var log = new RecordingLogger();
        var reporter = new CapStallReporter(log);

        reporter.Report(Blocked("wi-1", "wi-2"), maxConcurrent: 2);
        reporter.Report(Blocked("wi-2", "wi-3"), maxConcurrent: 2);

        Assert.Equal(2, log.At(LogLevel.Information).Count);
    }

    [Fact]
    public void Announces_again_after_the_stall_clears_and_returns()
    {
        // Otherwise the second stall is silent for as long as the process lives.
        var log = new RecordingLogger();
        var reporter = new CapStallReporter(log);

        reporter.Report(Blocked("wi-1"), maxConcurrent: 1);
        reporter.Report(new PollCycleResult(), maxConcurrent: 1);
        reporter.Report(Blocked("wi-1"), maxConcurrent: 1);

        Assert.Equal(2, log.At(LogLevel.Information).Count);
    }

    [Fact]
    public void Says_nothing_when_the_pass_was_not_blocked()
    {
        // Slots can be full with an empty Ready queue. Nothing is being held up,
        // so there is nothing to report at any level.
        var log = new RecordingLogger();
        var reporter = new CapStallReporter(log);

        reporter.Report(new PollCycleResult { SlotHolders = new[] { "wi-1" } }, maxConcurrent: 1);

        Assert.Empty(log.Messages);
    }

    [Fact]
    public void Announces_again_after_a_reset()
    {
        // Reset is what the scheduler calls when it loses sight of the state — a
        // failed pass, or the poller switched off and back on. Coming back to
        // the same stall is news again, not a repeat.
        var log = new RecordingLogger();
        var reporter = new CapStallReporter(log);

        reporter.Report(Blocked("wi-1"), maxConcurrent: 1);
        reporter.Reset();
        reporter.Report(Blocked("wi-1"), maxConcurrent: 1);

        Assert.Equal(2, log.At(LogLevel.Information).Count);
        Assert.Empty(log.At(LogLevel.Debug));
    }

    private static PollCycleResult Blocked(params string[] slotHolders)
        => new() { BlockedByCap = true, SlotHolders = slotHolders };

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Messages { get; } = new();

        public List<string> At(LogLevel level)
            => Messages.Where(m => m.Level == level).Select(m => m.Text).ToList();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add((logLevel, formatter(state, exception)));
    }
}
