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
    private readonly List<string> _worktrees = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IConfiguration Configuration => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:WorktreesPath"] = Path.Combine(_root, "worktrees") })
        .Build();

    private string StoredFolder => PreviewVolumes.RepositoryFolder(Configuration, _repository);

    private LocalRunHost Host()
    {
        var worktrees = new Mock<IRepositoryManager>();
        worktrees.Setup(r => r.CreateWorktreeAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((string path, string _) => path);
        return new LocalRunHost(repositories: worktrees.Object, configuration: Configuration);
    }

    private async Task<string> RunAsync(LocalRunHost host, string name, bool keep = true)
    {
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "worktrees", name)).FullName;
        _worktrees.Add(worktree);
        await host.CreateAsync(new RunEnvironmentRequest(Guid.NewGuid(), worktree, name, _repository, KeepPreviewData: keep), Ct);
        return worktree;
    }

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

        Assert.Equal(LocalRunHost.PreviewDataCopyOf(worktree), copy);
        Assert.Equal("old", File.ReadAllText(Path.Combine(copy!, "data", "seed")));
        File.WriteAllText(Path.Combine(copy!, "data", "seed"), "changed in the run");
        Assert.Equal("old", File.ReadAllText(Path.Combine(StoredFolder, "data", "seed")));

        await host.DeleteAsync(new RunEnvironment(Guid.NewGuid(), worktree, _repository), Ct);

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

        await host.DeleteAsync(new RunEnvironment(Guid.NewGuid(), worktree), Ct);

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
    public async Task A_copy_replaced_by_a_link_is_not_put_back_and_the_stored_copy_is_unchanged()
    {
        Store("data/seed", "old");
        var host = Host();
        var worktree = await RunAsync(host, "wt-a");
        var copy = LocalRunHost.PreviewDataCopyOf(worktree);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_root, "only-ild-may-read")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "secret"), "s");
        Directory.Delete(copy, recursive: true);
        Directory.CreateSymbolicLink(copy, elsewhere);

        await host.DeleteAsync(new RunEnvironment(Guid.NewGuid(), worktree, _repository), Ct);

        Assert.Equal("old", File.ReadAllText(Path.Combine(StoredFolder, "data", "seed")));
        Assert.False(File.Exists(Path.Combine(StoredFolder, "secret")));
        Assert.True(File.Exists(Path.Combine(elsewhere, "secret")));
    }

    public void Dispose()
    {
        foreach (var worktree in _worktrees)
            try { Directory.Delete(LocalRunHost.PreviewDataCopyOf(worktree), recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
