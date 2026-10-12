using System.Net;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// <c>on_new_commits</c>: the heartbeat fires it when the pull request's head is
/// not the head the run last saw — once per push, never again on a re-park at
/// the same head (the edge-state baseline a re-park clears is not what it reads),
/// and never on the first look, when there is no earlier head to have moved from.
/// </summary>
public class PrStatusPollServiceNewCommitsTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";
    private const string OldHead = "1111111111111111111111111111111111111111";
    private const string NewHead = "2222222222222222222222222222222222222222";

    private static RemotePrSnapshot Snapshot(string? head, RemotePrCiStatus ci = RemotePrCiStatus.Passed)
        => new("t", "b", "open", false, null, null, ci, Array.Empty<RemotePrCheck>(), false, false,
            Array.Empty<RemotePrConversationEntry>(), DateTime.UtcNow, head);

    private sealed class Harness
    {
        public LoopRun Run { get; }
        public LoopRunNode RunNode { get; }
        public Mock<ILoopRunStore> Runs { get; } = new();
        public Mock<IRemoteProvider> Remote { get; } = new();
        public Mock<ILoopEngine> Engine { get; } = new();

        public Harness(RemotePrSnapshot? stored, params string[] wired)
        {
            var loopNodeId = Guid.NewGuid();
            Run = new LoopRun
            {
                Id = Guid.NewGuid(), WorkItemId = "wi-1", PrUrl = PrUrl, Status = LoopRunStatus.WaitingHuman,
                CurrentNodeId = loopNodeId,
                PrSnapshot = stored is null ? null : PrSnapshotJson.Serialize(stored),
            };
            RunNode = new LoopRunNode
            {
                Id = Guid.NewGuid(), LoopRunId = Run.Id, LoopNodeId = loopNodeId, Status = LoopRunNodeStatus.WaitingHuman,
            };
            Runs.Setup(s => s.GetPrAwaitingMergeRunsAsync()).ReturnsAsync(() => new[] { Run });
            Runs.Setup(s => s.GetRunNodeAsync(Run.Id, loopNodeId)).ReturnsAsync(RunNode);
            Runs.Setup(s => s.GetEdgesForNodeIdsAsync(It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(wired
                .Select(name => new LoopNodeEdge
                {
                    Id = Guid.NewGuid(), SourceNodeId = loopNodeId, TargetNodeId = Guid.NewGuid(),
                    EdgeType = EdgeType.Custom, Name = name,
                })
                .ToArray());
        }

        public async Task PollAsync(RemotePrSnapshot current)
        {
            Remote.Setup(r => r.GetPullRequestSnapshotAsync("https://github.com/team/repo", "7")).ReturnsAsync(current);
            await new PrStatusPollService(Runs.Object, Remote.Object, Engine.Object, Mock.Of<IRunNotifier>(),
                NullLogger<PrStatusPollService>.Instance).PollOnceAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>What a park at the PR node does to the heartbeat's memory.</summary>
        public void Repark() => Run.PrPolledEdgeStates = null;

        public void VerifyNewCommitsFired(Times times)
            => Engine.Verify(e => e.SignalNodeResultAsync(Run.Id, RunNode.Id,
                It.Is<NodeSignal>(s => s.EdgeName == LoopOutputs.OnNewCommits)), times);
    }

    [Fact]
    public async Task A_push_fires_once_with_the_new_head_and_not_again_at_the_same_head()
    {
        var h = new Harness(Snapshot(OldHead), LoopOutputs.OnNewCommits);

        await h.PollAsync(Snapshot(NewHead));

        h.Engine.Verify(e => e.SignalNodeResultAsync(h.Run.Id, h.RunNode.Id,
            It.Is<NodeSignal>(s => s.EdgeName == LoopOutputs.OnNewCommits && s.Output!.Contains(NewHead))), Times.Once);

        await h.PollAsync(Snapshot(NewHead));
        h.VerifyNewCommitsFired(Times.Once());
    }

    [Fact]
    public async Task A_re_park_at_the_same_head_fires_nothing_while_ci_wired_back_would()
    {
        var h = new Harness(Snapshot(NewHead), LoopOutputs.OnNewCommits);
        h.Repark();

        await h.PollAsync(Snapshot(NewHead));

        h.VerifyNewCommitsFired(Times.Never());

        // The contrast the edge exists for: the same re-park re-fires a CI state.
        var ci = new Harness(Snapshot(NewHead), LoopOutputs.OnNewCommits, LoopOutputs.OnCiPassed);
        ci.Repark();
        await ci.PollAsync(Snapshot(NewHead));
        ci.Engine.Verify(e => e.SignalNodeResultAsync(ci.Run.Id, ci.RunNode.Id,
            It.Is<NodeSignal>(s => s.EdgeName == LoopOutputs.OnCiPassed)), Times.Once);
        ci.VerifyNewCommitsFired(Times.Never());
    }

    [Fact]
    public async Task The_first_poll_after_attaching_or_creating_fires_nothing()
    {
        var h = new Harness(stored: null, LoopOutputs.OnNewCommits);

        await h.PollAsync(Snapshot(NewHead));

        h.VerifyNewCommitsFired(Times.Never());
        Assert.Equal(NewHead, PrSnapshotJson.TryParse(h.Run.PrSnapshot)!.HeadSha);
    }

    [Fact]
    public async Task An_unwired_on_new_commits_never_fires()
    {
        var h = new Harness(Snapshot(OldHead));

        await h.PollAsync(Snapshot(NewHead));

        h.Engine.Verify(e => e.SignalNodeResultAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NodeSignal>()), Times.Never);
    }

    [Fact]
    public async Task A_snapshot_without_a_head_is_not_a_move()
    {
        var h = new Harness(Snapshot(OldHead), LoopOutputs.OnNewCommits);

        await h.PollAsync(Snapshot(head: null));

        h.VerifyNewCommitsFired(Times.Never());
    }

    // ---- The head each provider's snapshot carries ----

    private sealed class Forge(Func<string, string?> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = answer(Uri.UnescapeDataString(request.RequestUri!.ToString()));
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static RemoteProviderService Service(TestDb db, string type, string url, Func<string, string?> answer)
    {
        db.Context.RemoteProviders.Add(new RemoteProvider { Id = Guid.NewGuid(), Name = type, Type = type, Url = url, ApiKey = "k" });
        db.Context.SaveChanges();
        return new RemoteProviderService(db.Providers,
            new IRemoteGitProviderAdapter[]
            {
                new GitHubRemoteGitProviderAdapter(), new ForgejoRemoteGitProviderAdapter(), new AzureDevOpsRemoteGitProviderAdapter(),
            },
            new HttpClient(new Forge(answer)));
    }

    [Fact]
    public async Task A_github_snapshot_carries_the_pull_requests_head_commit()
    {
        using var db = new TestDb();
        var service = Service(db, "GitHub", "https://github.com", u => u.EndsWith("/pulls/7", StringComparison.Ordinal)
            ? "{\"number\":7,\"state\":\"open\",\"mergeable\":true,\"head\":{\"ref\":\"f\",\"sha\":\"" + NewHead + "\"}}"
            : u.Contains("check-runs", StringComparison.Ordinal) ? "{\"total_count\":0,\"check_runs\":[]}"
            : u.Contains("/status", StringComparison.Ordinal) ? "{\"state\":\"pending\",\"statuses\":[]}"
            : "[]");

        var snapshot = await service.GetPullRequestSnapshotAsync("https://github.com/team/repo", "7");

        Assert.Equal(NewHead, snapshot!.HeadSha);
    }

    [Fact]
    public async Task An_azure_snapshot_carries_the_last_merge_source_commit()
    {
        using var db = new TestDb();
        var service = Service(db, "AzureDevOps", "https://dev.azure.com/contoso", u =>
            u.Contains("/pullrequests/7?", StringComparison.Ordinal)
                ? "{\"pullRequestId\":7,\"status\":\"active\",\"mergeStatus\":\"succeeded\",\"lastMergeSourceCommit\":{\"commitId\":\"" + NewHead + "\"}}"
                : "{\"value\":[],\"count\":0}");

        var snapshot = await service.GetPullRequestSnapshotAsync("https://dev.azure.com/contoso/widgets/_git/app", "7");

        Assert.Equal(NewHead, snapshot!.HeadSha);
    }
}
