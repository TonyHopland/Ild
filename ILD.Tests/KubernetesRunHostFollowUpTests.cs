using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Kubernetes;
using ILD.Core.Services.RunHosts.Local;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Tests.Fakes;
using Moq;

namespace ILD.Tests;

public sealed class KubernetesRunHostFollowUpTests
{
    [Fact]
    public async Task A_failed_run_can_be_retried_so_the_sweep_keeps_its_pod_and_an_ended_run_loses_it()
    {
        using var db = new TestDb();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        var ids = new Dictionary<LoopRunStatus, Guid>();
        foreach (var status in Enum.GetValues<LoopRunStatus>())
        {
            ids[status] = Guid.NewGuid();
            db.Context.LoopRuns.Add(new LoopRun
            {
                Id = ids[status], WorkItemId = "WI-" + status, LoopTemplateVersionId = version.Id, Status = status, RecoveryPolicy = RecoveryPolicy.AutoResume,
            });
        }
        db.Context.SaveChanges();

        var kept = await ILD.Api.Services.LiveRunIds.NotEndedAsync(db.Context, TestContext.Current.CancellationToken);

        Assert.Contains(ids[LoopRunStatus.Failed], kept);
        Assert.Contains(ids[LoopRunStatus.Running], kept);
        Assert.Contains(ids[LoopRunStatus.WaitingHuman], kept);
        Assert.DoesNotContain(ids[LoopRunStatus.Completed], kept);
        Assert.DoesNotContain(ids[LoopRunStatus.Cancelled], kept);
    }

    [Fact]
    public async Task The_sweep_keeps_the_pods_of_the_runs_the_host_is_told_to_retain_even_when_the_caller_calls_them_dead()
    {
        await using var api = await FakeKubernetesApi.StartAsync();
        var failed = Guid.NewGuid();
        var ended = Guid.NewGuid();
        foreach (var run in new[] { failed, ended })
            api.Seed("secrets", KubernetesNames.ObjectName(run), "main", run);
        var options = KubernetesRunHostOptions.From(n => n switch { "ILD_API_URL" => "http://ild:8080", "ILD_K8S_RUNNER_IMAGE" => "i", "ILD_K8S_INSTANCE" => "main", _ => null });
        var host = new KubernetesRunHost(api.Client(), options, FakeKubernetesApi.Namespace, new ProcessRunner())
        {
            RetainedRuns = _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid> { failed }),
        };

        var leftovers = await host.ListLeftoversAsync(new HashSet<Guid>(), TestContext.Current.CancellationToken);

        Assert.Equal([ended.ToString("N")], leftovers.Select(l => l.Location));
    }

    [Fact]
    public async Task Kill_all_ends_only_the_commands_started_for_the_run_and_leaves_a_preview_with_no_owner()
    {
        await using var api = await FakeKubernetesApi.StartAsync();
        var run = Guid.NewGuid();
        api.Seed("pods", KubernetesNames.ObjectName(run), "main", run);
        var options = KubernetesRunHostOptions.From(n => n switch { "ILD_API_URL" => "http://ild:8080", "ILD_K8S_RUNNER_IMAGE" => "i", _ => null });
        var host = new KubernetesRunHost(api.Client(), options, FakeKubernetesApi.Namespace, new ProcessRunner());
        var wd = RunPaths.WorkingDirectory(run);
        api.OnExec = e => e.ExitAsync(0);

        using var owned = await host.StartCommandAsync(RunCommand.Prepared(new System.Diagnostics.ProcessStartInfo("agent") { WorkingDirectory = wd }, run), TestContext.Current.CancellationToken);
        using var preview = await host.StartCommandAsync(RunCommand.Prepared(new System.Diagnostics.ProcessStartInfo("npm") { WorkingDirectory = wd }), TestContext.Current.CancellationToken);
        await host.KillAllAsync(run, TestContext.Current.CancellationToken);

        var ownedId = ((ExecRunProcess)owned).CommandId;
        var previewId = ((ExecRunProcess)preview).CommandId;
        Assert.StartsWith(run.ToString("N") + "/", ownedId);
        Assert.StartsWith("unowned/", previewId);
        var kill = api.Execs.Last();
        Assert.Equal(KubernetesRunHost.KillAllScript, kill.Command[2]);
        Assert.Equal(run.ToString("N"), kill.Command[^1]);
        Assert.Contains("\"/run/ild/procs/$1\"/*", kill.Command[2]);
    }

    [Fact]
    public void A_run_in_a_pod_keeps_the_claude_session_under_the_pods_home_and_a_local_run_under_ours()
    {
        var run = Guid.NewGuid();
        var pod = new Mock<IRunHost>();
        pod.Setup(h => h.RunHomeFor(It.IsAny<string>())).Returns(RunPaths.HomeDirectory(run));
        var worktree = RunPaths.WorkingDirectory(run);
        var local = new Mock<IRunHost>();

        var inPod = ClaudeCodeAdapter.SessionFilePath(pod.Object, worktree, "s1", ProcessEnvironment.Current);
        var onDisk = ClaudeCodeAdapter.SessionFilePath(local.Object, "/worktrees/a", "s1", ProcessEnvironment.Current);

        Assert.Equal($"{RunPaths.HomeDirectory(run)}/.claude/projects/{ClaudeCodeAdapter.EncodeWorktreePath(worktree)}/s1.jsonl", inPod);
        Assert.Equal(ClaudeCodeAdapter.GetSessionFilePath("/worktrees/a", "s1", ProcessEnvironment.Current), onDisk);
    }

    [Fact]
    public void The_pod_host_says_a_pod_path_runs_its_clis_at_the_pods_home_and_nothing_else_does()
    {
        var run = Guid.NewGuid();
        var options = KubernetesRunHostOptions.From(n => n switch { "ILD_API_URL" => "http://ild:8080", "ILD_K8S_RUNNER_IMAGE" => "i", _ => null });
        var host = new KubernetesRunHost(new k8s.Kubernetes(new k8s.KubernetesClientConfiguration { Host = "http://127.0.0.1:1" }), options, "ns", new ProcessRunner());

        Assert.Equal(RunPaths.HomeDirectory(run), host.RunHomeFor(RunPaths.WorkingDirectory(run)));
        Assert.Null(host.RunHomeFor("/worktrees/a"));
        Assert.Null(new LocalRunHost().RunHomeFor("/worktrees/a"));
    }
}
