using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Local;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ILD.Tests;

/// <summary>
/// On the local host a run works on its own copy of the preview data its repository keeps, a folder in scratch:
/// filled from the stored copy when the run is made, put back when the run ends cleanly, and removed either way.
/// </summary>
public sealed class LocalRunHostPreviewDataTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ild-local-preview-data-").FullName;
    private readonly Guid _repository = Guid.NewGuid();
    private readonly Dictionary<string, Guid> _runs = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IConfiguration Configuration => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:WorktreesPath"] = Path.Combine(_root, "worktrees") })
        .Build();

    private string StoredFolder => PreviewVolumes.RepositoryFolder(Configuration, _repository);

    private readonly Mock<IRepositoryManager> _worktrees = Worktrees();

    private static Mock<IRepositoryManager> Worktrees()
    {
        var worktrees = new Mock<IRepositoryManager>();
        worktrees.Setup(r => r.CreateWorktreeAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((string path, string _) => path);
        worktrees.Setup(r => r.DestroyWorktreeAsync(It.IsAny<string>())).Returns((string path) =>
        {
            Directory.Delete(path, recursive: true);
            return Task.CompletedTask;
        });
        return worktrees;
    }

    /// <summary>A local host as one ILD process has it; another call is the host of ILD after a restart, which made none of the runs before.</summary>
    private LocalRunHost Host() => new(repositories: _worktrees.Object, configuration: Configuration);

    private LoopRun Run(string worktree) => new() { Id = _runs[worktree], WorkItemId = "wi-a", WorktreePath = worktree, RepositoryId = _repository };

    private RunReclaimer Reclaimer(LocalRunHost host) => new(_worktrees.Object, new Mock<IProviderStore>().Object, host);

    private async Task<string> RunAsync(LocalRunHost host, string name, bool keep = true)
    {
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "worktrees", name)).FullName;
        _runs[worktree] = Guid.NewGuid();
        await host.CreateAsync(new RunEnvironmentRequest(_runs[worktree], worktree, name, _repository, KeepPreviewData: keep), Ct);
        return worktree;
    }

    private string Copy(string worktree) => LocalRunHost.PreviewDataCopyOf(_runs[worktree], worktree);

    private void Store(string path, string content)
    {
        var file = Path.Combine(StoredFolder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }

    [Fact]
    public async Task A_run_works_on_a_copy_filled_from_the_store_which_a_clean_end_puts_back_and_removes()
    {
        Store("data/seed", "old");
        var host = Host();
        var worktree = await RunAsync(host, "wt-a");
        var copy = await host.PreviewDataFolderAsync(_repository, worktree, Ct);

        Assert.Equal(Copy(worktree), copy);
        Assert.Equal("old", File.ReadAllText(Path.Combine(copy!, "data", "seed")));
        File.WriteAllText(Path.Combine(copy!, "data", "seed"), "changed in the run");
        Assert.Equal("old", File.ReadAllText(Path.Combine(StoredFolder, "data", "seed")));

        await host.DeleteAsync(new RunEnvironment(_runs[worktree], worktree, _repository), Ct);

        Assert.Equal("changed in the run", File.ReadAllText(Path.Combine(StoredFolder, "data", "seed")));
        Assert.False(Directory.Exists(copy));
    }

    [Fact]
    public async Task A_run_ended_without_its_repository_is_removed_without_putting_its_copy_back()
    {
        Store("data/seed", "old");
        var host = Host();
        var worktree = await RunAsync(host, "wt-a");
        var copy = (await host.PreviewDataFolderAsync(_repository, worktree, Ct))!;
        File.WriteAllText(Path.Combine(copy, "data", "seed"), "changed in the run");

        await host.DeleteAsync(new RunEnvironment(_runs[worktree], worktree), Ct);

        Assert.Equal("old", File.ReadAllText(Path.Combine(StoredFolder, "data", "seed")));
        Assert.False(Directory.Exists(copy));
    }

    [Fact]
    public async Task A_run_made_without_kept_preview_data_has_no_copy()
    {
        Store("data/seed", "old");
        var host = Host();
        var worktree = await RunAsync(host, "wt-a", keep: false);

        Assert.Null(await host.PreviewDataFolderAsync(_repository, worktree, Ct));
    }

    [Fact]
    public async Task A_copy_that_cannot_be_unpacked_leaves_the_stored_copy_as_it_was()
    {
        Store("data/seed", "old");
        var store = new PreviewDataStore(PreviewVolumes.Root(Configuration));

        var replaced = await store.ReplaceAsync(_repository, new MemoryStream("not a tar archive"u8.ToArray()), Ct);

        Assert.False(replaced);
        Assert.Equal("old", File.ReadAllText(Path.Combine(StoredFolder, "data", "seed")));
        Assert.Equal([_repository.ToString("D")], Directory.GetFileSystemEntries(PreviewVolumes.Root(Configuration)).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Links_that_leave_the_data_are_dropped_when_it_is_stored_and_links_inside_are_kept()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "data", "pkg"));
        File.WriteAllText(Path.Combine(source, "data", "pkg", "tool"), "t");
        Directory.CreateDirectory(Path.Combine(source, "data", ".bin"));
        File.CreateSymbolicLink(Path.Combine(source, "data", ".bin", "tool"), "../pkg/tool");
        File.CreateSymbolicLink(Path.Combine(source, "data", "absolute.log"), "/etc/passwd");
        File.CreateSymbolicLink(Path.Combine(source, "data", "up.log"), "../../elsewhere");
        File.CreateSymbolicLink(Path.Combine(source, "data", "pkg", "round.log"), "../.bin/../../x");
        using var archive = new MemoryStream();
        await PreviewDataStore.PackFolderAsync(source, archive, Ct);
        archive.Position = 0;
        var store = new PreviewDataStore(PreviewVolumes.Root(Configuration));

        Assert.True(await store.ReplaceAsync(_repository, archive, Ct));

        Assert.Equal("../pkg/tool", new FileInfo(Path.Combine(StoredFolder, "data", ".bin", "tool")).LinkTarget);
        Assert.False(File.Exists(Path.Combine(StoredFolder, "data", "absolute.log")) || new FileInfo(Path.Combine(StoredFolder, "data", "absolute.log")).LinkTarget != null);
        Assert.Null(new FileInfo(Path.Combine(StoredFolder, "data", "up.log")).LinkTarget);
        Assert.Null(new FileInfo(Path.Combine(StoredFolder, "data", "pkg", "round.log")).LinkTarget);
    }

    [Fact]
    public async Task A_copy_replaced_by_a_link_is_not_put_back_and_the_stored_copy_is_unchanged()
    {
        Store("data/seed", "old");
        var host = Host();
        var worktree = await RunAsync(host, "wt-a");
        var copy = Copy(worktree);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_root, "only-ild-may-read")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "secret"), "s");
        Directory.Delete(copy, recursive: true);
        Directory.CreateSymbolicLink(copy, elsewhere);

        await host.DeleteAsync(new RunEnvironment(_runs[worktree], worktree, _repository), Ct);

        Assert.Equal("old", File.ReadAllText(Path.Combine(StoredFolder, "data", "seed")));
        Assert.False(File.Exists(Path.Combine(StoredFolder, "secret")));
        Assert.True(File.Exists(Path.Combine(elsewhere, "secret")));
    }

    [Fact]
    public async Task Reclaiming_a_run_this_process_made_puts_its_copy_back()
    {
        Store("data/seed", "old");
        var host = Host();
        var worktree = await RunAsync(host, "wt-a");
        File.WriteAllText(Path.Combine(Copy(worktree), "data", "seed"), "changed in the run");

        Assert.True(await Reclaimer(host).ReclaimLocalStateAsync(Run(worktree)));

        Assert.Equal("changed in the run", File.ReadAllText(Path.Combine(StoredFolder, "data", "seed")));
        Assert.False(Directory.Exists(Copy(worktree)));
    }

    [Fact]
    public async Task Reclaiming_a_run_after_ild_restarted_removes_its_copy_without_putting_it_back()
    {
        Store("data/seed", "old");
        var worktree = await RunAsync(Host(), "wt-a");
        File.WriteAllText(Path.Combine(Copy(worktree), "data", "seed"), "half-written when ILD stopped");

        Assert.True(await Reclaimer(Host()).ReclaimLocalStateAsync(Run(worktree)));

        Assert.Equal("old", File.ReadAllText(Path.Combine(StoredFolder, "data", "seed")));
        Assert.False(Directory.Exists(Copy(worktree)));
    }

    [Fact]
    public async Task The_startup_sweep_removes_the_copy_of_a_run_that_is_gone_without_putting_it_back()
    {
        Store("data/seed", "old");
        var before = Host();
        var gone = await RunAsync(before, "wt-gone");
        var live = await RunAsync(before, "wt-live");
        File.WriteAllText(Path.Combine(Copy(gone), "data", "seed"), "half-written");
        var restarted = Host();

        var leftovers = LocalRunHost.ListLeftovers(
                new HashSet<Guid> { _runs[live] }, Path.Combine(_root, "agent-read"), AgentIsolation.ScratchRoot, DateTime.UtcNow.AddMinutes(1), null)
            .Leftovers.Where(l => l.Kind == LocalRunHost.PreviewDataCopyKind).ToList();

        Assert.DoesNotContain(leftovers, l => l.Location == Copy(live));
        Assert.True(await restarted.DeleteLeftoverAsync(Assert.Single(leftovers, l => l.Location == Copy(gone)), Ct));
        Assert.False(Directory.Exists(Copy(gone)));
        Assert.True(Directory.Exists(Copy(live)));
        Assert.Equal("old", File.ReadAllText(Path.Combine(StoredFolder, "data", "seed")));
    }

    [Fact]
    public async Task The_startup_sweep_leaves_copies_made_since_it_started()
    {
        var worktree = await RunAsync(Host(), "wt-a");

        var leftovers = LocalRunHost.ListLeftovers(
            new HashSet<Guid>(), Path.Combine(_root, "agent-read"), AgentIsolation.ScratchRoot, DateTime.UtcNow.AddMinutes(-1), null).Leftovers;

        Assert.DoesNotContain(leftovers, l => l.Location == Copy(worktree));
    }

    public void Dispose()
    {
        foreach (var worktree in _runs.Keys)
            try { Directory.Delete(Copy(worktree), recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
