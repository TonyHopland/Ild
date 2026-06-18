using System.Diagnostics;
using System.Reflection;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ILD.Tests;

[Collection("Git")]
public class CombinedPreviewSweeperTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _base;
    private readonly Guid _repoId = Guid.NewGuid();
    private readonly Repository _repo;
    private readonly RepositoryManager _repoMgr;
    private readonly CombinedPreviewRegistry _registry = new();
    private readonly Dictionary<string, WorkItemView> _views = new(StringComparer.Ordinal);

    public CombinedPreviewSweeperTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ild-cpsweep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);

        var origin = Path.Combine(_tmp, "origin");
        Directory.CreateDirectory(origin);
        Git(origin, "init", "-b", "main");
        Git(origin, "config", "user.email", "t@t.io");
        Git(origin, "config", "user.name", "Tester");
        File.WriteAllText(Path.Combine(origin, "base.txt"), "base\n");
        Git(origin, "add", "-A");
        Git(origin, "commit", "-m", "seed");

        _base = Path.Combine(_tmp, "base");
        Git(_tmp, "clone", origin, _base);
        Git(_base, "config", "user.email", "t@t.io");
        Git(_base, "config", "user.name", "Tester");

        _repo = new Repository
        {
            Id = _repoId,
            Name = "r",
            CloneUrl = origin,
            DefaultBranch = "main",
            WorktreesPath = _base,
            RemoteProviderId = Guid.NewGuid(),
        };
        _repoMgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Sweep_reaps_an_idle_combined_preview_but_keeps_member_branches()
    {
        AddMember("1", "Alpha", CreateBranch("ild/wi-1-run-a", "alpha.txt", "alpha\n"));
        AddMember("2", "Beta", CreateBranch("ild/wi-2-run-b", "beta.txt", "beta\n"));
        var preview = RunningPreview();
        var started = await BuildService(preview).StartAsync(new CombinedPreviewStartRequest { WorkItemIds = { "1", "2" } });
        Assert.True(Directory.Exists(started.WorktreePath!));

        // Age the entry past the 24h default window.
        _registry.All().Single().LastActivityAt = DateTime.UtcNow - TimeSpan.FromHours(48);

        var reaped = await InvokeSweepOnceAsync(BuildSweeper(preview), DateTime.UtcNow);

        Assert.Equal(1, reaped);
        Assert.False(Directory.Exists(started.WorktreePath!));
        Assert.False(await _repoMgr.LocalBranchExistsAsync(_base, started.IntegrationBranch));
        Assert.Empty(_registry.All());
        // Member branches survive untouched.
        Assert.True(await _repoMgr.LocalBranchExistsAsync(_base, "ild/wi-1-run-a"));
        Assert.True(await _repoMgr.LocalBranchExistsAsync(_base, "ild/wi-2-run-b"));
        preview.Verify(p => p.StopAsync(started.WorktreePath!, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Sweep_leaves_a_recently_active_preview_alone()
    {
        AddMember("1", "Alpha", CreateBranch("ild/wi-1-run-a", "alpha.txt", "alpha\n"));
        AddMember("2", "Beta", CreateBranch("ild/wi-2-run-b", "beta.txt", "beta\n"));
        var preview = RunningPreview();
        var started = await BuildService(preview).StartAsync(new CombinedPreviewStartRequest { WorkItemIds = { "1", "2" } });

        // LastActivityAt was just stamped by Start — within the window.
        var reaped = await InvokeSweepOnceAsync(BuildSweeper(preview), DateTime.UtcNow);

        Assert.Equal(0, reaped);
        Assert.True(Directory.Exists(started.WorktreePath!));
        Assert.Single(_registry.All());
    }

    [Fact]
    public async Task Sweep_reaps_an_orphan_worktree_with_no_registry_entry()
    {
        // Simulate a restart: the integration worktree survives on disk but the
        // in-memory registry is empty.
        var worktreePath = await _repoMgr.CreateWorktreeFromAsync(_base, "ild/combined-9-9", "origin/main");
        Directory.SetLastWriteTimeUtc(worktreePath, DateTime.UtcNow - TimeSpan.FromHours(48));
        Assert.True(await _repoMgr.LocalBranchExistsAsync(_base, "ild/combined-9-9"));

        var reaped = await InvokeSweepOnceAsync(BuildSweeper(RunningPreview()), DateTime.UtcNow);

        Assert.Equal(1, reaped);
        Assert.False(Directory.Exists(worktreePath));
        Assert.False(await _repoMgr.LocalBranchExistsAsync(_base, "ild/combined-9-9"));
    }

    [Fact]
    public async Task Sweep_is_disabled_when_retention_is_zero()
    {
        AddMember("1", "Alpha", CreateBranch("ild/wi-1-run-a", "alpha.txt", "alpha\n"));
        var preview = RunningPreview();
        var started = await BuildService(preview).StartAsync(new CombinedPreviewStartRequest { WorkItemIds = { "1" } });
        _registry.All().Single().LastActivityAt = DateTime.UtcNow - TimeSpan.FromHours(48);

        var sweeper = BuildSweeper(preview, retentionHours: "0");
        var reaped = await InvokeSweepOnceAsync(sweeper, DateTime.UtcNow);

        Assert.Equal(0, reaped);
        Assert.True(Directory.Exists(started.WorktreePath!));
    }

    // --- helpers -----------------------------------------------------------

    private void AddMember(string id, string title, string? branch)
        => _views[id] = new WorkItemView { Id = id, Title = title, BranchName = branch, RepositoryId = _repoId };

    private string CreateBranch(string branch, string file, string content)
    {
        Git(_base, "checkout", "-b", branch, "origin/main");
        File.WriteAllText(Path.Combine(_base, file), content);
        Git(_base, "add", "-A");
        Git(_base, "commit", "-m", $"work on {branch}");
        Git(_base, "checkout", "main");
        return branch;
    }

    private CombinedPreviewService BuildService(Mock<IWorktreePreviewService> preview)
    {
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => _views.TryGetValue(id, out var v) ? v : null);
        workItems.Setup(m => m.GetDependenciesAsync(It.IsAny<string>()))
            .ReturnsAsync((IReadOnlyList<WorkItemView>)new List<WorkItemView>());

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(_repoId)).ReturnsAsync(_repo);
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

        return new CombinedPreviewService(
            workItems.Object, providerStore.Object, _repoMgr, preview.Object, _registry, new ConfigurationBuilder().Build());
    }

    private CombinedPreviewSweeper BuildSweeper(Mock<IWorktreePreviewService> preview, string? retentionHours = null)
    {
        var settings = new Dictionary<string, string?>();
        if (retentionHours != null)
            settings["App:CombinedPreviewRetentionHours"] = retentionHours;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new CombinedPreviewSweeper(_registry, _repoMgr, preview.Object, config);
    }

    // SweepOnceAsync is internal; invoke it via reflection like WorktreeRetentionSweeperTests.
    private static async Task<int> InvokeSweepOnceAsync(CombinedPreviewSweeper sweeper, DateTime now)
    {
        var task = (Task<int>)typeof(CombinedPreviewSweeper)
            .GetMethod("SweepOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sweeper, new object[] { now, CancellationToken.None })!;
        return await task;
    }

    private static Mock<IWorktreePreviewService> RunningPreview()
    {
        var preview = new Mock<IWorktreePreviewService>();
        var running = new WorktreePreviewResponse { Configured = true, State = "running" };
        preview.Setup(p => p.StartAsync(It.IsAny<string>(), It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(running);
        preview.Setup(p => p.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(running);
        preview.Setup(p => p.StopAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorktreePreviewResponse { State = "stopped" });
        return preview;
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {p.StandardError.ReadToEnd()}");
    }
}
