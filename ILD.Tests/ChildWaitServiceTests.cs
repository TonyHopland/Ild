using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// ILD's wait for the processes an agent left running: it wakes the agent when they have ended, with how they ended and the tail of their
/// output; at the timeout it stops them and says so; and if waking fails it leaves the run for a person.
/// </summary>
public class ChildWaitServiceTests
{
    private static readonly RunChildProcess Tests = new(4321, "dotnet test ILD.Tests", "/tmp/tests.output");

    private sealed class Setup : IDisposable
    {
        public TestDb Db { get; } = new();
        public FakeRunHost Host { get; } = new();
        public Mock<ILoopEngine> Engine { get; } = new();

        public LoopRun Seed(
            DateTime? startedAt = null, HaltReason haltReason = HaltReason.WaitingForChildren,
            IReadOnlyList<RunChildProcess>? processes = null)
        {
            var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
            var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
            Db.Context.LoopTemplates.Add(template);
            Db.Context.LoopTemplateVersions.Add(version);
            var run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = Guid.NewGuid().ToString(),
                LoopTemplateVersionId = version.Id,
                RecoveryPolicy = RecoveryPolicy.AutoResume,
                Status = LoopRunStatus.WaitingHuman,
                IsHalted = true,
                HaltReason = haltReason,
                ChildWaitJson = new ChildWaitState(startedAt ?? DateTime.UtcNow, processes ?? [Tests]).ToJson(),
            };
            Db.Context.LoopRuns.Add(run);
            Db.Context.SaveChanges();
            return run;
        }

        public Task SweepAsync()
        {
            var services = new ServiceCollection();
            services.AddSingleton(Db.LoopRuns);
            services.AddSingleton<ISchedulerSettingsService>(new SchedulerSettingsService(Db.Settings));
            services.AddSingleton<IRunHost>(Host);
            var provider = services.BuildServiceProvider();
            return new ChildWaitService(provider.GetRequiredService<IServiceScopeFactory>(), Engine.Object, NullLogger<ChildWaitService>.Instance)
                .SweepOnceAsync(CancellationToken.None);
        }

        public void Dispose() => Db.Dispose();
    }

    private static string? NoteOf(Mock<ILoopEngine> engine)
    {
        string? note = null;
        foreach (var call in engine.Invocations.Where(i => i.Method.Name == nameof(ILoopEngine.WakeFromChildWaitAsync)))
            note = (string?)call.Arguments[1];
        return note;
    }

    [Fact]
    public async Task Does_nothing_while_the_processes_are_still_running_inside_the_timeout()
    {
        using var s = new Setup();
        s.Host.LeaveRunning(Tests.Pid, Tests.CommandLine);
        s.Seed(startedAt: DateTime.UtcNow.AddMinutes(-5));

        await s.SweepAsync();

        s.Engine.Verify(e => e.WakeFromChildWaitAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        Assert.Empty(s.Host.StoppedProcesses);
        Assert.Contains(Tests.Pid, s.Host.RunningPids);
    }

    [Fact]
    public async Task Wakes_the_agent_when_the_processes_have_ended_with_the_tail_of_their_output()
    {
        using var s = new Setup();
        var run = s.Seed(startedAt: DateTime.UtcNow.AddMinutes(-12));
        s.Host.Tails[Tests.OutputPath!] = "Passed! total: 400, failed: 0\n";

        await s.SweepAsync();

        s.Engine.Verify(e => e.WakeFromChildWaitAsync(run.Id, It.IsAny<string>()), Times.Once);
        var note = NoteOf(s.Engine)!;
        Assert.Contains("have finished", note);
        Assert.Contains("pid 4321", note);
        Assert.Contains("dotnet test ILD.Tests", note);
        Assert.Contains("Passed! total: 400, failed: 0", note);
        Assert.Empty(s.Host.StoppedProcesses);
    }

    [Fact]
    public async Task At_the_timeout_it_stops_the_processes_and_tells_the_agent_they_were_killed_by_a_signal()
    {
        using var s = new Setup();
        s.Host.LeaveRunning(Tests.Pid, Tests.CommandLine, Tests.OutputPath);
        s.Host.Tails[Tests.OutputPath!] = "Starting test run...\n";
        var run = s.Seed(startedAt: DateTime.UtcNow.AddMinutes(-61));

        await s.SweepAsync();

        Assert.Equal([Tests], s.Host.StoppedProcesses);
        s.Engine.Verify(e => e.WakeFromChildWaitAsync(run.Id, It.IsAny<string>()), Times.Once);
        var note = NoteOf(s.Engine)!;
        Assert.Contains("had not finished after 61 minute(s), so ILD stopped them", note);
        // The signal is passed on rather than the run parking in silence.
        Assert.Contains("killed by a signal", note);
        Assert.Contains("143", note);
        Assert.Contains("137", note);
        Assert.Contains("Starting test run...", note);
    }

    [Fact]
    public async Task The_timeout_is_the_configured_number_of_minutes_and_defaults_to_an_hour()
    {
        Assert.Equal(60, AppSettingKeys.DefaultChildrenWaitMinutes);
        using var s = new Setup();
        s.Host.LeaveRunning(Tests.Pid, Tests.CommandLine);
        s.Seed(startedAt: DateTime.UtcNow.AddMinutes(-20));

        await s.SweepAsync();
        s.Engine.Verify(e => e.WakeFromChildWaitAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);

        await s.Db.Settings.UpsertAsync(AppSettingKeys.ChildrenWaitMinutes, "15", TestContext.Current.CancellationToken);
        await s.SweepAsync();

        s.Engine.Verify(e => e.WakeFromChildWaitAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Once);
        Assert.Equal([Tests], s.Host.StoppedProcesses);
    }

    [Fact]
    public async Task When_waking_the_agent_fails_the_run_is_left_for_a_person()
    {
        using var s = new Setup();
        var run = s.Seed(startedAt: DateTime.UtcNow.AddMinutes(-12));
        s.Engine.Setup(e => e.WakeFromChildWaitAsync(run.Id, It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("session is gone"));

        await s.SweepAsync();

        s.Engine.Verify(e => e.ReleaseChildWaitToHumanAsync(run.Id, It.Is<string>(reason => reason.Contains("session is gone"))), Times.Once);
    }

    [Theory]
    [InlineData(HaltReason.Human)]
    [InlineData(HaltReason.Throttled)]
    [InlineData(HaltReason.Shutdown)]
    [InlineData(HaltReason.MaxAiTraversals)]
    public async Task Never_wakes_a_park_that_is_not_its_own(HaltReason reason)
    {
        using var s = new Setup();
        s.Seed(startedAt: DateTime.UtcNow.AddMinutes(-120), haltReason: reason);

        await s.SweepAsync();

        s.Engine.Verify(e => e.WakeFromChildWaitAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task One_failing_run_does_not_strand_the_others()
    {
        using var s = new Setup();
        var first = s.Seed(startedAt: DateTime.UtcNow.AddMinutes(-12));
        var second = s.Seed(startedAt: DateTime.UtcNow.AddMinutes(-12));
        s.Engine.Setup(e => e.WakeFromChildWaitAsync(first.Id, It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("x"));
        s.Engine.Setup(e => e.ReleaseChildWaitToHumanAsync(first.Id, It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("y"));

        await s.SweepAsync();

        s.Engine.Verify(e => e.WakeFromChildWaitAsync(second.Id, It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task A_host_that_cannot_tell_is_not_taken_for_processes_that_have_ended()
    {
        using var s = new Setup();
        s.Host.LeaveRunning(Tests.Pid, Tests.CommandLine);
        s.Host.StillRunningFailure = new InvalidOperationException("exec failed");
        s.Seed(startedAt: DateTime.UtcNow.AddMinutes(-5));

        await s.SweepAsync();

        s.Engine.Verify(e => e.WakeFromChildWaitAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        s.Engine.Verify(e => e.ReleaseChildWaitToHumanAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        Assert.Empty(s.Host.StoppedProcesses);
    }

    [Fact]
    public async Task A_host_that_still_cannot_tell_at_the_timeout_leaves_the_run_for_a_person()
    {
        using var s = new Setup();
        s.Host.StillRunningFailure = new InvalidOperationException("exec failed");
        var run = s.Seed(startedAt: DateTime.UtcNow.AddMinutes(-61));

        await s.SweepAsync();

        s.Engine.Verify(e => e.WakeFromChildWaitAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        s.Engine.Verify(e => e.ReleaseChildWaitToHumanAsync(run.Id, It.Is<string>(reason => reason.Contains("exec failed"))), Times.Once);
    }

    [Fact]
    public async Task A_paused_run_is_left_alone()
    {
        using var s = new Setup();
        var run = s.Seed(startedAt: DateTime.UtcNow.AddMinutes(-120));
        run.IsPaused = true;
        s.Db.Context.SaveChanges();

        await s.SweepAsync();

        s.Engine.Verify(e => e.WakeFromChildWaitAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    // -- The message ------------------------------------------------------------------------------

    [Fact]
    public void The_message_says_each_process_ended_and_that_ild_does_not_know_its_exit_code()
    {
        var note = ChildWaitMessages.Build([new ChildOutcome(Tests, StoppedByIld: false, "line 1\nline 2\n")], TimeSpan.FromMinutes(12), timedOut: false);

        Assert.Contains("have finished (after 12 minute(s))", note);
        Assert.Contains("pid 4321: `dotnet test ILD.Tests`", note);
        Assert.Contains("exit code is not available", note);
        Assert.Contains("  line 2", note);
    }

    [Fact]
    public void The_message_carries_only_the_end_of_a_long_output()
    {
        var output = string.Join('\n', Enumerable.Range(1, 500).Select(i => $"line {i}"));

        var note = ChildWaitMessages.Build([new ChildOutcome(Tests, false, output)], TimeSpan.FromMinutes(1), false);

        Assert.Contains("line 500", note);
        Assert.Contains("line 461", note);
        Assert.DoesNotContain("line 460\n", note);
    }
}
