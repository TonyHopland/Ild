using System.Text.Json.Nodes;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Container;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Kubernetes;
using ILD.Data.Enums;
using ILD.Tests.Fakes;
using k8s.Models;

namespace ILD.Tests;

/// <summary>Why a run's container ended is read from its host, said in plain words, and attached to the command that died with it.</summary>
public sealed class RunTerminationTests
{
    private const long FourGi = 4L * 1024 * 1024 * 1024;

    [Fact]
    public void An_out_of_memory_kill_is_described_with_the_limit_when_known()
    {
        Assert.Equal(
            "The run ran out of memory: its container was killed at the 4Gi limit (`ILD_RUN_MEMORY_LIMIT`).",
            new RunTermination(RunTerminationKind.OutOfMemory, FourGi).Describe());
        Assert.StartsWith("The run ran out of memory: its container was killed for exceeding its memory limit", new RunTermination(RunTerminationKind.OutOfMemory).Describe());
    }

    [Fact]
    public void Only_an_out_of_memory_kill_has_a_reason_of_its_own()
    {
        Assert.Equal(HumanFeedbackReasons.OutOfMemory, new RunTermination(RunTerminationKind.OutOfMemory).HumanFeedbackReason);
        Assert.Equal(HumanFeedbackReasons.NodeFailed, new RunTermination(RunTerminationKind.Evicted).HumanFeedbackReason);
    }

    [Theory]
    [InlineData(RunTerminationKind.Evicted, "evicted")]
    [InlineData(RunTerminationKind.NodeLost, "lost with its node")]
    [InlineData(RunTerminationKind.Deleted, "was deleted")]
    public void Other_ways_a_container_ended_are_named(RunTerminationKind kind, string words)
        => Assert.Contains(words, new RunTermination(kind).Describe());

    // ---- the command ----

    private sealed class StubExec(int? status) : IExecStream
    {
        public Stream Stdout { get; } = new MemoryStream();
        public Stream? Stderr { get; } = new MemoryStream();
        public Task<int?> Status { get; } = Task.FromResult(status);
        public Task WriteStdinAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => Task.CompletedTask;
        public Task CloseStdinAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeAsync(int cols, int rows, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static async Task<(RunTermination? Termination, int Reads)> EndedWith(int? status, RunTermination? reported)
    {
        var reads = 0;
        using var process = new ExecRunProcess(new StubExec(status), "pod", "owner/id", redirectInput: false, (_, _) => Task.FromResult(true),
            readTermination: (_, _) => { reads++; return Task.FromResult(reported); });
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        return (process.Termination, reads);
    }

    [Theory]
    [InlineData(137)]
    [InlineData(null)]
    public async Task A_command_killed_or_cut_off_asks_the_host_why_and_carries_the_answer(int? status)
    {
        var oom = new RunTermination(RunTerminationKind.OutOfMemory, FourGi);

        var (termination, reads) = await EndedWith(status, oom);

        Assert.Equal(oom, termination);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_command_that_ended_on_its_own_does_not_ask(int status)
    {
        var (termination, reads) = await EndedWith(status, new RunTermination(RunTerminationKind.OutOfMemory, FourGi));

        Assert.Null(termination);
        Assert.Equal(0, reads);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The host keeps what it read of a pod that has ended, for after the pod is replaced.
    private static async Task RememberAsync(InMemoryRunHost host, Guid run, RunTermination termination)
    {
        host.RunEndedBy = termination;
        await host.GetTerminationAsync(run, null, Ct);
        host.RunEndedBy = null;
    }

    [Fact]
    public async Task An_old_out_of_memory_kill_is_not_reported_for_a_later_unrelated_exit_after_the_pod_is_replaced()
    {
        var host = new InMemoryRunHost();
        var run = Guid.NewGuid();
        await RememberAsync(host, run, new RunTermination(RunTerminationKind.OutOfMemory, FourGi, EndedAt: DateTimeOffset.UtcNow.AddMinutes(-5)));
        var laterCommandStarted = DateTimeOffset.UtcNow.AddMinutes(-1);

        Assert.Null(await host.GetTerminationAsync(run, laterCommandStarted, Ct));
        Assert.Equal(RunTerminationKind.OutOfMemory, (await host.GetTerminationAsync(run, DateTimeOffset.UtcNow.AddMinutes(-10), Ct))!.Kind);
    }

    [Fact]
    public async Task A_remembered_ending_with_no_time_is_not_used_for_a_command_that_started_after_it_was_recorded()
    {
        var host = new InMemoryRunHost();
        var run = Guid.NewGuid();
        var earlier = DateTimeOffset.UtcNow.AddSeconds(-1);
        await RememberAsync(host, run, new RunTermination(RunTerminationKind.OutOfMemory, FourGi));

        Assert.NotNull(await host.GetTerminationAsync(run, earlier, Ct));
        Assert.Null(await host.GetTerminationAsync(run, DateTimeOffset.UtcNow.AddSeconds(1), Ct));
    }

    [Fact]
    public async Task A_live_ending_is_read_and_a_run_that_is_gone_is_reported_deleted()
    {
        var host = new InMemoryRunHost();
        var run = Guid.NewGuid();
        host.RunEndedBy = new RunTermination(RunTerminationKind.OutOfMemory, FourGi);
        Assert.Equal(RunTerminationKind.OutOfMemory, (await host.GetTerminationAsync(run, ct: Ct))!.Kind);

        host.RunEndedBy = null;
        host.RunPresent = false;
        Assert.Equal(RunTerminationKind.Deleted, (await host.GetTerminationAsync(Guid.NewGuid(), ct: Ct))!.Kind);
    }

    [Fact]
    public async Task A_host_that_cannot_answer_leaves_the_command_as_it_ended()
    {
        using var process = new ExecRunProcess(new StubExec(137), "pod", "owner/id", redirectInput: false, (_, _) => Task.FromResult(true),
            readTermination: (_, _) => throw new InvalidOperationException("unreachable"));

        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Null(process.Termination);
        Assert.Equal(137, process.ExitCode);
    }

    // ---- Kubernetes ----

    private static V1Pod Pod(V1ContainerState? state, V1ContainerState? last = null, string? reason = null, string? phase = "Failed") => new()
    {
        Metadata = new V1ObjectMeta { Name = "run" },
        Spec = new V1PodSpec
        {
            Containers = [new V1Container { Name = "run", Resources = new V1ResourceRequirements { Limits = new Dictionary<string, ResourceQuantity> { ["memory"] = new("4Gi") } } }],
        },
        Status = new V1PodStatus
        {
            Phase = phase,
            Reason = reason,
            ContainerStatuses = [new V1ContainerStatus { Name = "run", State = state, LastState = last }],
        },
    };

    private static V1ContainerState Terminated(string reason, int exitCode = 137)
        => new() { Terminated = new V1ContainerStateTerminated { Reason = reason, ExitCode = exitCode, FinishedAt = new DateTime(2026, 11, 15, 13, 59, 14, DateTimeKind.Utc) } };

    [Fact]
    public void A_pod_whose_container_was_OOMKilled_reports_an_out_of_memory_kill_at_the_pod_limit()
    {
        var termination = KubernetesRunHost.TerminationOf(Pod(Terminated("OOMKilled")));

        Assert.Equal(RunTerminationKind.OutOfMemory, termination!.Kind);
        Assert.Equal(FourGi, termination.LimitBytes);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 13, 59, 14, TimeSpan.Zero), termination.EndedAt);
    }

    [Fact]
    public void An_OOMKilled_container_that_was_started_again_is_found_in_its_last_state()
        => Assert.Equal(RunTerminationKind.OutOfMemory,
            KubernetesRunHost.TerminationOf(Pod(new V1ContainerState { Running = new V1ContainerStateRunning() }, Terminated("OOMKilled"), phase: "Running"))!.Kind);

    [Theory]
    [InlineData("Evicted", RunTerminationKind.Evicted)]
    [InlineData("NodeLost", RunTerminationKind.NodeLost)]
    public void A_pod_that_was_evicted_or_lost_its_node_says_so(string reason, RunTerminationKind kind)
        => Assert.Equal(kind, KubernetesRunHost.TerminationOf(Pod(null, reason: reason))!.Kind);

    [Fact]
    public void A_pod_that_is_running_has_not_ended()
        => Assert.Null(KubernetesRunHost.TerminationOf(Pod(new V1ContainerState { Running = new V1ContainerStateRunning() }, phase: "Running")));

    // ---- Docker and Podman ----

    [Fact]
    public void A_container_the_engine_says_was_OOMKilled_reports_an_out_of_memory_kill_at_its_limit()
    {
        var inspected = JsonNode.Parse($$$"""{"State":{"Status":"exited","ExitCode":137,"OOMKilled":true,"FinishedAt":"2026-11-15T13:59:14.5Z"},"HostConfig":{"Memory":{{{FourGi}}}}}""")!;

        var termination = ContainerRunHost.TerminationOf(inspected);

        Assert.Equal(RunTerminationKind.OutOfMemory, termination!.Kind);
        Assert.Equal(FourGi, termination.LimitBytes);
    }

    [Fact]
    public void A_container_that_is_running_has_not_ended()
        => Assert.Null(ContainerRunHost.TerminationOf(JsonNode.Parse("""{"State":{"Status":"running","OOMKilled":false},"HostConfig":{"Memory":0}}""")!));
}
