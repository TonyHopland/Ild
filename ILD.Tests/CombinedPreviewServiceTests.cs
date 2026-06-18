using System.Diagnostics;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ILD.Tests;

[Collection("Git")]
public class CombinedPreviewServiceTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _base;
    private readonly Guid _repoId = Guid.NewGuid();
    private readonly Repository _repo;
    private readonly RepositoryManager _repoMgr;

    private readonly Dictionary<string, WorkItemView> _views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _deps = new(StringComparer.Ordinal);

    public CombinedPreviewServiceTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ild-combined-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);

        // An origin the base repo can clone, so origin/main (the integration base)
        // resolves the same way a cloned-on-demand base repo does in production.
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
    public async Task Start_composes_clean_merge_and_starts_preview()
    {
        AddMember("1", "Alpha", CreateBranch("ild/wi-1-run-a", "alpha.txt", "alpha\n"));
        AddMember("2", "Beta", CreateBranch("ild/wi-2-run-b", "beta.txt", "beta\n"));
        var preview = RunningPreview();
        var service = BuildService(preview);

        var result = await service.StartAsync(Request("1", "2"));

        Assert.Equal("running", result.State);
        Assert.All(result.Members, m => Assert.Equal("clean", m.MergeStatus));
        Assert.NotNull(result.Preview);
        Assert.Equal("running", result.Preview!.State);

        // Both member files land in the one integration worktree — a real composition.
        Assert.True(File.Exists(Path.Combine(result.WorktreePath!, "alpha.txt")));
        Assert.True(File.Exists(Path.Combine(result.WorktreePath!, "beta.txt")));
        preview.Verify(p => p.StartAsync(result.WorktreePath!, It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Start_resolves_the_members_current_run_branch_not_an_older_one()
    {
        // An earlier run's branch and a later run's branch both exist locally; the
        // work item resolves to the later one, so only its file may be composed.
        CreateBranch("ild/wi-1-run-old", "old.txt", "old\n");
        var current = CreateBranch("ild/wi-1-run-new", "new.txt", "new\n");
        AddMember("1", "Alpha", current);
        var service = BuildService(RunningPreview());

        var result = await service.StartAsync(Request("1"));

        Assert.Equal("running", result.State);
        Assert.True(File.Exists(Path.Combine(result.WorktreePath!, "new.txt")));
        Assert.False(File.Exists(Path.Combine(result.WorktreePath!, "old.txt")));
        Assert.Equal("ild/wi-1-run-new", result.Members.Single().BranchName);
    }

    [Fact]
    public async Task Start_merges_dependencies_before_dependents_regardless_of_selection_order()
    {
        AddMember("A", "First", CreateBranch("ild/wi-A-run", "a.txt", "a\n"));
        AddMember("B", "Second", CreateBranch("ild/wi-B-run", "b.txt", "b\n"));
        _deps["B"] = new List<string> { "A" }; // B depends on A

        var service = BuildService(RunningPreview());

        // Selection order is [B, A]; dependency order must still merge A first.
        var result = await service.StartAsync(Request("B", "A"));
        Assert.Equal("running", result.State);

        var subjects = GitOutput(result.WorktreePath!, "log", "--format=%s")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var aIdx = Array.FindIndex(subjects, s => s.Contains("ild/wi-A-run"));
        var bIdx = Array.FindIndex(subjects, s => s.Contains("ild/wi-B-run"));
        Assert.True(aIdx >= 0 && bIdx >= 0);
        // git log is newest-first, so the later merge (B) appears before A.
        Assert.True(aIdx > bIdx, "dependency A must be merged before dependent B");

        // Merge order is surfaced to the UI: A precedes B in the members list.
        Assert.Equal(new[] { "A", "B" }, result.Members.Select(m => m.WorkItemId).ToArray());
    }

    [Fact]
    public async Task Start_reports_first_conflict_and_does_not_start_preview()
    {
        AddMember("1", "Alpha", CreateBranch("ild/wi-1-run-a", "base.txt", "one\n"));
        AddMember("2", "Beta", CreateBranch("ild/wi-2-run-b", "base.txt", "two\n"));
        var preview = RunningPreview();
        var service = BuildService(preview);

        var result = await service.StartAsync(Request("1", "2"));

        Assert.Equal("conflict", result.State);
        Assert.Null(result.Preview);
        var clean = result.Members.Single(m => m.WorkItemId == "1");
        var conflicted = result.Members.Single(m => m.WorkItemId == "2");
        Assert.Equal("clean", clean.MergeStatus);
        Assert.Equal("conflict", conflicted.MergeStatus);
        Assert.Contains("base.txt", conflicted.ConflictedFiles);
        // Default Stop mode aborts the conflicting merge — no preview is launched.
        preview.Verify(p => p.StartAsync(It.IsAny<string>(), It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Start_skipping_the_conflicting_member_yields_a_partial_preview()
    {
        AddMember("1", "Alpha", CreateBranch("ild/wi-1-run-a", "base.txt", "one\n"));
        AddMember("2", "Beta", CreateBranch("ild/wi-2-run-b", "base.txt", "two\n"));
        var preview = RunningPreview();
        var service = BuildService(preview);

        var request = Request("1", "2");
        request.Skip = new List<string> { "2" };
        var result = await service.StartAsync(request);

        Assert.Equal("partial", result.State);
        Assert.Equal("clean", result.Members.Single(m => m.WorkItemId == "1").MergeStatus);
        Assert.Equal("skipped", result.Members.Single(m => m.WorkItemId == "2").MergeStatus);
        Assert.NotNull(result.Preview);
        preview.Verify(p => p.StartAsync(It.IsAny<string>(), It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Get_marks_preview_stale_after_a_member_rebranches()
    {
        AddMember("1", "Alpha", CreateBranch("ild/wi-1-run-a", "alpha.txt", "alpha\n"));
        AddMember("2", "Beta", CreateBranch("ild/wi-2-run-b", "beta.txt", "beta\n"));
        var service = BuildService(RunningPreview());

        await service.StartAsync(Request("1", "2"));

        // Member 1 re-runs: its current branch changes. The composed preview is now stale.
        _views["1"].BranchName = "ild/wi-1-run-a2";
        var status = await service.GetAsync(new[] { "1", "2" });

        Assert.True(status.Stale);
        Assert.True(status.Members.Single(m => m.WorkItemId == "1").Stale);
        Assert.False(status.Members.Single(m => m.WorkItemId == "2").Stale);
    }

    [Fact]
    public async Task Stop_tears_down_integration_worktree_and_branch_but_keeps_member_branches()
    {
        AddMember("1", "Alpha", CreateBranch("ild/wi-1-run-a", "alpha.txt", "alpha\n"));
        AddMember("2", "Beta", CreateBranch("ild/wi-2-run-b", "beta.txt", "beta\n"));
        var preview = RunningPreview();
        var service = BuildService(preview);

        var started = await service.StartAsync(Request("1", "2"));
        Assert.True(Directory.Exists(started.WorktreePath!));

        var stopped = await service.StopAsync(new[] { "1", "2" });

        Assert.Equal("stopped", stopped.State);
        Assert.False(Directory.Exists(started.WorktreePath!));
        Assert.False(await _repoMgr.LocalBranchExistsAsync(_base, started.IntegrationBranch));
        // Member branches and their work are untouched.
        Assert.True(await _repoMgr.LocalBranchExistsAsync(_base, "ild/wi-1-run-a"));
        Assert.True(await _repoMgr.LocalBranchExistsAsync(_base, "ild/wi-2-run-b"));
        preview.Verify(p => p.StopAsync(started.WorktreePath!, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Start_rejects_a_selection_spanning_two_repositories()
    {
        AddMember("1", "Alpha", CreateBranch("ild/wi-1-run-a", "alpha.txt", "alpha\n"));
        AddMember("2", "Beta", CreateBranch("ild/wi-2-run-b", "beta.txt", "beta\n"));
        _views["2"].RepositoryId = Guid.NewGuid(); // different repo

        var service = BuildService(RunningPreview());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(Request("1", "2")));
        Assert.Contains("share one repository", ex.Message);
    }

    // --- helpers -----------------------------------------------------------

    private CombinedPreviewStartRequest Request(params string[] ids)
        => new() { WorkItemIds = ids.ToList() };

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
            .ReturnsAsync((string id) =>
                (IReadOnlyList<WorkItemView>)(_deps.TryGetValue(id, out var d) ? d : new List<string>())
                    .Select(depId => _views[depId])
                    .ToList());

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(_repoId)).ReturnsAsync(_repo);
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

        return new CombinedPreviewService(
            workItems.Object,
            providerStore.Object,
            _repoMgr,
            preview.Object,
            new CombinedPreviewRegistry(),
            new ConfigurationBuilder().Build());
    }

    private static Mock<IWorktreePreviewService> RunningPreview()
    {
        var preview = new Mock<IWorktreePreviewService>();
        var running = new WorktreePreviewResponse
        {
            Configured = true,
            State = "running",
            Services = new List<WorktreePreviewServiceResponse>
            {
                new() { Name = "web", Status = "running", Port = 3000, PublicUrl = "http://127.0.0.1:3000" },
            },
        };
        preview.Setup(p => p.StartAsync(It.IsAny<string>(), It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(running);
        preview.Setup(p => p.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(running);
        preview.Setup(p => p.StopAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorktreePreviewResponse { State = "stopped" });
        return preview;
    }

    private static string GitOutput(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.StandardOutput.ReadToEnd();
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
