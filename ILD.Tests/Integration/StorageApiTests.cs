using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using static ILD.Tests.Integration.StorageApiTests;

namespace ILD.Tests.Integration;

/// <summary>
/// Settings &gt; Storage: the cleanup of worktrees whose work items are Done, and
/// who may call the storage endpoints. Freed sizes are checked against real files
/// on disk — allocated blocks and hard links.
/// </summary>
[SupportedOSPlatform("linux")]
public class StorageApiTests
{
    internal const long KiB = 1024;
    internal const long MiB = 1024 * KiB;

    // Directories and inode rounding the tests do not count by hand.
    internal const long Slack = 128 * KiB;

    [Fact]
    public async Task Cleanup_lists_every_finished_unpinned_run_of_a_Done_item_with_nothing_active_and_what_removing_it_frees()
    {
        var items = new WorkItems();
        items.Add("wi-done", "Done item", RemoteWorkItemStatus.Done);
        items.Add("wi-busy", "Busy item", RemoteWorkItemStatus.Done);
        items.Add("wi-waiting", "Waiting item", RemoteWorkItemStatus.Done);
        items.Add("wi-open", "Open item", RemoteWorkItemStatus.HumanFeedback);
        await using var factory = new ApiFactory(configureServices: services =>
        {
            services.ReplaceSingleton(items.Client);
            services.ReplaceSingleton<IRunReclaimer>(new RecordingReclaimer());
        });
        var client = await factory.CreateAuthenticatedClientAsync();
        using var scratch = new TempDir();
        var worktrees = scratch.Path;
        var alpha = await SeedRepositoryAsync(factory, "alpha");

        string Tree(string name) { var p = Path.Combine(worktrees, name); Directory.CreateDirectory(p); return p; }
        var d1Path = Tree("d1");
        var d2Path = Tree("d2");
        var d3Path = Tree("d3");
        WriteFile(Path.Combine(d1Path, "own.bin"), 1 * MiB);
        WriteFile(Path.Combine(d1Path, "both.bin"), 2 * MiB);
        HardLink(Path.Combine(d1Path, "both.bin"), Path.Combine(d2Path, "both.bin"));
        WriteFile(Path.Combine(worktrees, ".store", "pkg.bin"), 4 * MiB);
        HardLink(Path.Combine(worktrees, ".store", "pkg.bin"), Path.Combine(d1Path, "pkg.bin"));
        WriteFile(Path.Combine(d3Path, "own.bin"), 512 * KiB);

        var d1 = await SeedRunAsync(factory, "wi-done", LoopRunStatus.Completed, d1Path, alpha);
        var d2 = await SeedRunAsync(factory, "wi-done", LoopRunStatus.Failed, d2Path, alpha);
        var d3 = await SeedRunAsync(factory, "wi-done", LoopRunStatus.Cancelled, d3Path, repositoryId: null);
        await SeedRunAsync(factory, "wi-done", LoopRunStatus.Completed, Tree("pinned"), alpha, retain: true);
        await SeedRunAsync(factory, "wi-done", LoopRunStatus.Completed, worktreePath: null, alpha);
        await SeedRunAsync(factory, "wi-busy", LoopRunStatus.Completed, Tree("busy-old"), alpha);
        await SeedRunAsync(factory, "wi-busy", LoopRunStatus.Running, Tree("busy-live"), alpha);
        await SeedRunAsync(factory, "wi-waiting", LoopRunStatus.Completed, Tree("waiting-old"), alpha);
        await SeedRunAsync(factory, "wi-waiting", LoopRunStatus.WaitingHuman, Tree("waiting-live"), alpha);
        await SeedRunAsync(factory, "wi-open", LoopRunStatus.Completed, Tree("open"), alpha);
        await SeedRunAsync(factory, "wi-gone", LoopRunStatus.Completed, Tree("gone"), alpha);

        var response = await client.GetAsync("/api/v1/storage/worktree-cleanup", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(worktrees, body, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(body);
        var candidates = doc.RootElement.GetProperty("candidates").EnumerateArray()
            .ToDictionary(c => c.GetProperty("runId").GetGuid());
        Assert.Equal(new[] { d1, d2, d3 }.OrderBy(id => id), candidates.Keys.OrderBy(id => id));

        var first = candidates[d1];
        Assert.Equal("wi-done", first.GetProperty("workItemId").GetString());
        Assert.Equal("Done item", first.GetProperty("workItemTitle").GetString());
        Assert.Equal(alpha, first.GetProperty("repositoryId").GetGuid());
        Assert.Equal("alpha", first.GetProperty("repositoryName").GetString());
        Assert.Equal(JsonValueKind.Null, candidates[d3].GetProperty("repositoryId").ValueKind);
        Assert.Equal(JsonValueKind.Null, candidates[d3].GetProperty("repositoryName").ValueKind);
        Assert.All(candidates.Values, c => Assert.False(c.GetProperty("incomplete").GetBoolean()));

        // Alone, d1 frees only own.bin: both.bin is still linked from d2 and
        // pkg.bin from the store. Together d1+d2 also free both.bin, never pkg.bin.
        AssertAbout(1 * MiB, Bytes(first));
        Assert.True(Bytes(candidates[d2]) < Slack);
        AssertAbout(512 * KiB, Bytes(candidates[d3]));
        AssertAbout(1 * MiB + 2 * MiB + 512 * KiB, doc.RootElement.GetProperty("totalBytes").GetInt64());
    }

    [Fact]
    public async Task Confirming_cleans_up_only_the_sent_runs_still_eligible_and_reports_each_one()
    {
        var items = new WorkItems();
        foreach (var id in new[] { "wi-a", "wi-b", "wi-c", "wi-d", "wi-e", "wi-f" })
            items.Add(id, "Item " + id, RemoteWorkItemStatus.Done);
        items.Add("wi-open", "Open item", RemoteWorkItemStatus.Ready);
        var reclaimer = new RecordingReclaimer();
        await using var factory = new ApiFactory(configureServices: services =>
        {
            services.ReplaceSingleton(items.Client);
            services.ReplaceSingleton<IRunReclaimer>(reclaimer);
        });
        var client = await factory.CreateAuthenticatedClientAsync();
        using var scratch = new TempDir();
        var worktrees = scratch.Path;
        var alpha = await SeedRepositoryAsync(factory, "alpha");

        async Task<Guid> Eligible(string workItemId)
        {
            var path = Path.Combine(worktrees, workItemId);
            WriteFile(Path.Combine(path, "own.bin"), 1 * MiB);
            return await SeedRunAsync(factory, workItemId, LoopRunStatus.Completed, path, alpha);
        }
        var cleaned = await Eligible("wi-a");
        var reopened = await Eligible("wi-b");
        var pinned = await Eligible("wi-c");
        var restarted = await Eligible("wi-d");
        var stuck = await Eligible("wi-e");
        var notSent = await Eligible("wi-f");
        var neverEligible = await SeedRunAsync(factory, "wi-open", LoopRunStatus.Completed, Path.Combine(worktrees, "open"), alpha);
        reclaimer.FailFor.Add(stuck);

        var listed = JsonDocument.Parse(await client.GetStringAsync("/api/v1/storage/worktree-cleanup", TestContext.Current.CancellationToken));
        Assert.Equal(
            new[] { cleaned, reopened, pinned, restarted, stuck, notSent }.OrderBy(id => id),
            listed.RootElement.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("runId").GetGuid()).OrderBy(id => id));

        // Between the list and the confirm, the world moves on.
        items.Add("wi-b", "Item wi-b", RemoteWorkItemStatus.HumanFeedback);
        await UpdateRunAsync(factory, pinned, r => r.Retain = true);
        await SeedRunAsync(factory, "wi-d", LoopRunStatus.Running, Path.Combine(worktrees, "wi-d-new"), alpha);
        var before = await ReadRunAsync(factory, stuck);
        var unknown = Guid.NewGuid();

        var response = await client.PostAsJsonAsync("/api/v1/storage/worktree-cleanup",
            new { runIds = new[] { cleaned, reopened, pinned, restarted, stuck, neverEligible, unknown } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var results = doc.RootElement.GetProperty("results").EnumerateArray()
            .ToDictionary(r => r.GetProperty("runId").GetGuid());
        Assert.Equal(7, results.Count);

        Assert.Equal("Cleaned", results[cleaned].GetProperty("outcome").GetString(), ignoreCase: true);
        // Measured before the reclaim took the directory away.
        AssertAbout(1 * MiB, Bytes(results[cleaned]));
        Assert.Equal(Bytes(results[cleaned]), doc.RootElement.GetProperty("totalBytes").GetInt64());
        foreach (var skipped in new[] { reopened, pinned, restarted, neverEligible, unknown })
        {
            Assert.Equal("Skipped", results[skipped].GetProperty("outcome").GetString(), ignoreCase: true);
            Assert.False(string.IsNullOrWhiteSpace(results[skipped].GetProperty("reason").GetString()));
        }
        Assert.Equal("Failed", results[stuck].GetProperty("outcome").GetString(), ignoreCase: true);

        Assert.Equal(new[] { cleaned, stuck }.OrderBy(id => id), reclaimer.Reclaimed.OrderBy(id => id));
        var after = await ReadRunAsync(factory, cleaned);
        Assert.NotNull(after);
        Assert.Null(after!.WorktreePath);
        Assert.Null(after.BranchName);
        var stuckAfter = await ReadRunAsync(factory, stuck);
        Assert.Equal(before!.WorktreePath, stuckAfter!.WorktreePath);
        Assert.Equal(before.BranchName, stuckAfter.BranchName);
        foreach (var untouched in new[] { reopened, pinned, restarted, notSent, neverEligible })
        {
            var run = await ReadRunAsync(factory, untouched);
            Assert.NotNull(run!.WorktreePath);
            Assert.NotNull(run.BranchName);
        }
    }

    [Fact]
    public async Task A_cleanup_request_without_valid_run_ids_is_refused_and_changes_nothing()
    {
        var items = new WorkItems();
        items.Add("wi-a", "Item", RemoteWorkItemStatus.Done);
        var reclaimer = new RecordingReclaimer();
        await using var factory = new ApiFactory(configureServices: services =>
        {
            services.ReplaceSingleton(items.Client);
            services.ReplaceSingleton<IRunReclaimer>(reclaimer);
        });
        var client = await factory.CreateAuthenticatedClientAsync();
        using var scratch = new TempDir();
        var path = Path.Combine(scratch.Path, "wi-a");
        Directory.CreateDirectory(path);
        var eligible = await SeedRunAsync(factory, "wi-a", LoopRunStatus.Completed, path, repositoryId: null);

        foreach (var json in new[] { "{}", "{\"runIds\":null}", "{\"runIds\":[]}", $"{{\"runIds\":[\"{eligible}\",\"not-a-guid\"]}}" })
        {
            var response = await client.PostAsync("/api/v1/storage/worktree-cleanup",
                new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()), json);
        }

        Assert.Empty(reclaimer.Reclaimed);
        Assert.Equal(path, (await ReadRunAsync(factory, eligible))!.WorktreePath);
    }

    [Fact]
    public async Task With_the_WorkItem_server_unreachable_both_cleanup_endpoints_answer_503_and_change_nothing()
    {
        var server = new Mock<IWorkItemServerClient>();
        var outage = new HttpRequestException("Connection refused (workitem-server:8081)");
        server.Setup(c => c.GetAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(outage);
        server.Setup(c => c.ListAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<RemoteWorkItemStatus?>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(outage);
        var reclaimer = new RecordingReclaimer();
        await using var factory = new ApiFactory(configureServices: services =>
        {
            services.ReplaceSingleton(server.Object);
            services.ReplaceSingleton<IRunReclaimer>(reclaimer);
        });
        var client = await factory.CreateAuthenticatedClientAsync();
        using var scratch = new TempDir();
        var path = Path.Combine(scratch.Path, "wi-a");
        Directory.CreateDirectory(path);
        var run = await SeedRunAsync(factory, "wi-a", LoopRunStatus.Completed, path, repositoryId: null);

        var list = await client.GetAsync("/api/v1/storage/worktree-cleanup", TestContext.Current.CancellationToken);
        var confirm = await client.PostAsJsonAsync("/api/v1/storage/worktree-cleanup", new { runIds = new[] { run } }, TestContext.Current.CancellationToken);

        foreach (var response in new[] { list, confirm })
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
        }
        Assert.Empty(reclaimer.Reclaimed);
        Assert.Equal(path, (await ReadRunAsync(factory, run))!.WorktreePath);
    }

    [Fact]
    public async Task Every_storage_endpoint_is_for_the_user_only()
    {
        var reclaimer = new RecordingReclaimer();
        await using var factory = new ApiFactory(configureServices: services => services.ReplaceSingleton<IRunReclaimer>(reclaimer));
        var anonymous = factory.CreateClient();
        var agent = factory.CreateClient();
        agent.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token);
        var ct = TestContext.Current.CancellationToken;

        foreach (var (caller, expected) in new[] { (anonymous, HttpStatusCode.Unauthorized), (agent, HttpStatusCode.Forbidden) })
        {
            Assert.Equal(expected, (await caller.GetAsync("/api/v1/storage", ct)).StatusCode);
            Assert.Equal(expected, (await caller.GetAsync("/api/v1/storage/worktree-cleanup", ct)).StatusCode);
            Assert.Equal(expected, (await caller.PostAsJsonAsync("/api/v1/storage/worktree-cleanup", new { runIds = new[] { Guid.NewGuid() } }, ct)).StatusCode);
        }
        Assert.Empty(reclaimer.Reclaimed);

        // ...and not merely refused because nothing answers there.
        var user = await factory.CreateAuthenticatedClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/storage", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/storage/worktree-cleanup", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.PostAsJsonAsync("/api/v1/storage/worktree-cleanup", new { runIds = new[] { Guid.NewGuid() } }, ct)).StatusCode);
    }

    // --- disk helpers ---

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldpath, string newpath);

    internal static void HardLink(string existing, string newPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        Assert.True(link(existing, newPath) == 0, $"link({existing}, {newPath}) failed: errno {Marshal.GetLastPInvokeError()}");
    }

    internal static void WriteFile(string path, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        using var stream = new FileStream(path, FileMode.CreateNew);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    internal static bool CanList(string directory)
    {
        try { _ = Directory.EnumerateFileSystemEntries(directory).ToList(); return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Allocated bytes under <paramref name="path"/>, each inode once, links not followed.</summary>
    internal static long DiskUsage(string path)
    {
        var start = new ProcessStartInfo("du") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-s", "-B1", path })
            start.ArgumentList.Add(arg);
        using var du = Process.Start(start)!;
        var output = du.StandardOutput.ReadToEnd();
        du.WaitForExit();
        Assert.Equal(0, du.ExitCode);
        return long.Parse(output.Split('\t')[0], CultureInfo.InvariantCulture);
    }

    internal static void AssertAbout(long expected, long actual)
        => Assert.True(actual >= expected && actual <= expected + Slack,
            $"expected {expected} bytes (+ up to {Slack} for directories), got {actual}");

    internal sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ild-storage-test-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    // --- response helpers ---

    internal static JsonElement Category(JsonDocument doc, string key)
        => doc.RootElement.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("key").GetString() == key);

    internal static IEnumerable<JsonElement> Entries(JsonElement category)
        => category.GetProperty("entries").EnumerateArray();

    internal static JsonElement Entry(JsonElement category, string key)
        => Entries(category).Single(e => e.GetProperty("key").GetString() == key);

    internal static JsonElement RepositoryEntry(JsonElement category, Guid repositoryId)
        => Entries(category).Single(e => e.GetProperty("repositoryId").ValueKind == JsonValueKind.String
            && e.GetProperty("repositoryId").GetGuid() == repositoryId);

    internal static long Bytes(JsonElement element) => element.GetProperty("bytes").GetInt64();

    // --- seeding ---

    internal static string DataRoot(ApiFactory factory)
        => Path.GetFullPath(factory.Services.GetRequiredService<IConfiguration>()["App:DataPath"]!);

    internal static string WorktreesRoot(ApiFactory factory)
        => Path.GetFullPath(factory.Services.GetRequiredService<IConfiguration>()["App:WorktreesPath"]!);

    internal static async Task<Guid> SeedRepositoryAsync(ApiFactory factory, string name, string? worktreesPath = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var provider = new RemoteProvider { Id = Guid.NewGuid(), Name = "prov-" + name, Type = "GitHub", Url = "https://example.com", ApiKey = "k" };
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = name,
            RemoteProviderId = provider.Id,
            CloneUrl = $"https://example.com/{name}.git",
            WorktreesPath = worktreesPath,
            CreatedAt = DateTime.UtcNow,
        };
        db.RemoteProviders.Add(provider);
        db.Repositories.Add(repo);
        await db.SaveChangesAsync();
        return repo.Id;
    }

    internal static async Task<Guid> SeedRunAsync(
        ApiFactory factory, string workItemId, LoopRunStatus status, string? worktreePath, Guid? repositoryId, bool retain = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"storage-{Guid.NewGuid():N}" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow };
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            LoopTemplateVersionId = version.Id,
            Status = status,
            StartedAt = DateTime.UtcNow.AddHours(-1),
            CompletedAt = status is LoopRunStatus.Running or LoopRunStatus.WaitingHuman ? null : DateTime.UtcNow,
            WorktreePath = worktreePath,
            BranchName = worktreePath == null ? null : "ild/" + Guid.NewGuid().ToString("N")[..12],
            RepositoryId = repositoryId,
            Retain = retain,
        };
        db.LoopTemplates.Add(template);
        db.LoopTemplateVersions.Add(version);
        db.LoopRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private static async Task UpdateRunAsync(ApiFactory factory, Guid runId, Action<LoopRun> change)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        change(await db.LoopRuns.SingleAsync(r => r.Id == runId));
        await db.SaveChangesAsync();
    }

    private static async Task<LoopRun?> ReadRunAsync(ApiFactory factory, Guid runId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().LoopRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId);
    }

    /// <summary>
    /// The WorkItem server as its client reports it (WorkItemServerClient: a 404
    /// is null), with statuses a test can change between two requests.
    /// </summary>
    private sealed class WorkItems
    {
        private readonly Dictionary<string, RemoteWorkItem> _items = new();
        private readonly Mock<IWorkItemServerClient> _client = new();

        public WorkItems()
        {
            _client.Setup(c => c.GetAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((WorkItemServerOptions _, string id, CancellationToken _) => _items.GetValueOrDefault(id));
            _client.Setup(c => c.ListAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<RemoteWorkItemStatus?>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((WorkItemServerOptions _, RemoteWorkItemStatus? status, IReadOnlyList<string>? _, CancellationToken _) =>
                    (IReadOnlyList<RemoteWorkItem>)_items.Values.Where(i => status == null || i.Status == status).ToList());
        }

        public IWorkItemServerClient Client => _client.Object;

        public void Add(string id, string title, RemoteWorkItemStatus status)
            => _items[id] = new RemoteWorkItem { Id = id, Title = title, Status = status };
    }

    /// <summary>
    /// Stands in for RunReclaimer: on success the worktree directory is gone, as
    /// it is after <c>git worktree remove</c>; on failure nothing changes.
    /// </summary>
    private sealed class RecordingReclaimer : IRunReclaimer
    {
        public List<Guid> Reclaimed { get; } = [];
        public HashSet<Guid> FailFor { get; } = [];

        public Task<bool> ReclaimLocalStateAsync(LoopRun run)
        {
            lock (Reclaimed) Reclaimed.Add(run.Id);
            if (FailFor.Contains(run.Id)) return Task.FromResult(false);
            if (run.WorktreePath != null && Directory.Exists(run.WorktreePath))
                Directory.Delete(run.WorktreePath, recursive: true);
            return Task.FromResult(true);
        }
    }
}

/// <summary>
/// Settings &gt; Storage: what the overview measures and where each byte is
/// attributed. Sizes are checked against real files on disk — allocated blocks,
/// hard links, sparse files and links — with <c>du</c>, which counts each inode
/// once and never follows a link, as the oracle for the whole data root.
///
/// <para>
/// Each host gets a data root of its own through <c>ILD_DATA_PATH</c>, which the
/// API reads while it builds — the factory's <c>Storage:DataRoot</c> arrives too
/// late for that read, so every other host shares one data root, which a byte
/// count cannot. The variable is process-wide, and so is the scratch root one,
/// hence the collection.
/// </para>
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
[SupportedOSPlatform("linux")]
public class StorageOverviewTests
{
    [Fact]
    public async Task The_overview_counts_each_file_once_by_its_allocated_size_and_attributes_worktrees_through_their_runs()
    {
        using var dataRoot = new TempDir();
        await using var factory = FactoryOn(dataRoot.Path);
        var client = await factory.CreateAuthenticatedClientAsync();
        var data = DataRoot(factory);
        Assert.Equal(dataRoot.Path, data);
        var worktrees = WorktreesRoot(factory);
        using var outside = new TempDir();

        var alpha = await SeedRepositoryAsync(factory, "alpha");
        var beta = await SeedRepositoryAsync(factory, "beta", worktreesPath: Path.Combine(data, "custom", "beta"));
        var gamma = await SeedRepositoryAsync(factory, "gamma");
        var deleted = Guid.NewGuid();

        var a1 = Path.Combine(worktrees, "a1");
        var a2 = Path.Combine(worktrees, "a2");
        WriteFile(Path.Combine(a1, "own.bin"), 256 * KiB);
        WriteFile(Path.Combine(a2, "own.bin"), 256 * KiB);
        // Both links inside one worktree: it is that worktree's own.
        WriteFile(Path.Combine(a1, "pair.bin"), 1 * MiB);
        HardLink(Path.Combine(a1, "pair.bin"), Path.Combine(a1, "sub", "pair.bin"));
        // A package-store file linked into two worktrees belongs to neither.
        WriteFile(Path.Combine(worktrees, ".pnpm-store", "pkg.bin"), 4 * MiB);
        HardLink(Path.Combine(worktrees, ".pnpm-store", "pkg.bin"), Path.Combine(a1, "pkg.bin"));
        HardLink(Path.Combine(worktrees, ".pnpm-store", "pkg.bin"), Path.Combine(a2, "pkg.bin"));
        WriteFile(Path.Combine(outside.Path, "big", "elsewhere.bin"), 8 * MiB);
        Directory.CreateSymbolicLink(Path.Combine(a1, "out-link"), Path.Combine(outside.Path, "big"));

        // A worktree outside the worktrees root, and a custom clone path, both
        // inside the data root: neither may also land in "other".
        var b1 = Path.Combine(data, "elsewhere", "b1");
        WriteFile(Path.Combine(b1, "own.bin"), 2 * MiB);
        WriteFile(Path.Combine(data, "custom", "beta", ".git", "objects.bin"), 2 * MiB);

        // A run worktree that is itself a link is not descended into.
        var c1 = Path.Combine(worktrees, "c1");
        Directory.CreateSymbolicLink(c1, Path.Combine(outside.Path, "big"));

        WriteFile(Path.Combine(data, "repos", alpha.ToString("N"), ".git", "objects.bin"), 1 * MiB);
        WriteFile(Path.Combine(data, "repos", deleted.ToString("N"), ".git", "objects.bin"), 256 * KiB);

        WriteFile(Path.Combine(data, "chat-sessions", "s1", "log.bin"), 128 * KiB);
        // Linked from outside every measured path: counted once, somewhere in chat.
        WriteFile(Path.Combine(data, "chat-sessions", "s1", "ext.bin"), 2 * MiB);
        HardLink(Path.Combine(data, "chat-sessions", "s1", "ext.bin"), Path.Combine(outside.Path, "ext-link.bin"));

        WriteFile(Path.Combine(data, "loose.bin"), 64 * KiB);
        using (var sparse = File.Create(Path.Combine(data, "sparse.img")))
            sparse.SetLength(1024 * MiB);

        await SeedRunAsync(factory, "wi-a1", LoopRunStatus.Completed, a1, alpha);
        await SeedRunAsync(factory, "wi-a2", LoopRunStatus.Completed, a2, alpha);
        // A second run naming the same directory: still one worktree on disk.
        await SeedRunAsync(factory, "wi-a3", LoopRunStatus.Failed, a2, alpha);
        await SeedRunAsync(factory, "wi-b1", LoopRunStatus.Completed, b1, beta);
        await SeedRunAsync(factory, "wi-c1", LoopRunStatus.Completed, c1, gamma);

        var response = await client.GetAsync("/api/v1/storage", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(Path.GetTempPath().TrimEnd('/'), body, StringComparison.Ordinal);
        Assert.DoesNotContain(data, body, StringComparison.Ordinal);
        Assert.DoesNotContain(outside.Path, body, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(body);
        var categories = doc.RootElement.GetProperty("categories").EnumerateArray().ToList();
        Assert.Equal(
            new[] { "agent-installs", "chat-sessions", "other", "repository-clones", "temp", "worktrees" },
            categories.Select(c => c.GetProperty("key").GetString()).OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(categories, c => Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("label").GetString())));
        // Temp is shared with every other test running now, so only the data root is quiet enough to pin.
        Assert.All(categories.Where(c => c.GetProperty("key").GetString() != "temp"),
            c => Assert.False(c.GetProperty("incomplete").GetBoolean()));

        var wt = Category(doc, "worktrees");
        var alphaWorktrees = RepositoryEntry(wt, alpha);
        Assert.Equal("alpha", alphaWorktrees.GetProperty("repositoryName").GetString());
        // own.bin twice + pair.bin once; not pkg.bin, not what out-link points at,
        // and a2 once although two runs name it.
        AssertAbout(256 * KiB * 2 + 1 * MiB, Bytes(alphaWorktrees));
        AssertAbout(2 * MiB, Bytes(RepositoryEntry(wt, beta)));
        Assert.True(Bytes(RepositoryEntry(wt, gamma)) < Slack);
        AssertAbout(4 * MiB, Bytes(Entry(wt, "shared")));
        var unowned = Entry(wt, "unowned");
        Assert.Equal(JsonValueKind.Null, unowned.GetProperty("repositoryId").ValueKind);
        Assert.True(Bytes(unowned) < Slack);
        Assert.Equal(Bytes(wt), Entries(wt).Sum(Bytes));

        var clones = Category(doc, "repository-clones");
        AssertAbout(1 * MiB, Bytes(RepositoryEntry(clones, alpha)));
        AssertAbout(2 * MiB, Bytes(RepositoryEntry(clones, beta)));
        var orphan = RepositoryEntry(clones, deleted);
        Assert.Equal(JsonValueKind.Null, orphan.GetProperty("repositoryName").ValueKind);
        AssertAbout(256 * KiB, Bytes(orphan));
        Assert.Equal(Bytes(clones), Entries(clones).Sum(Bytes));

        AssertAbout(128 * KiB + 2 * MiB, Bytes(Category(doc, "chat-sessions")));
        Assert.Equal(0, Bytes(Category(doc, "agent-installs")));
        // loose.bin and directories; not the 1 GiB sparse file's length, and not
        // the custom clone or the out-of-root worktree a second time.
        Assert.True(Bytes(Category(doc, "other")) < 1 * MiB, $"other = {Bytes(Category(doc, "other"))}");

        var temp = Category(doc, "temp");
        Assert.Equal(
            new[] { "agent-read", "private", "scratch" },
            Entries(temp).Select(e => e.GetProperty("key").GetString()).OrderBy(k => k, StringComparer.Ordinal));
        // The test process has no dedicated scratch root: it falls back to TMPDIR,
        // which is shared with everything else and so is not measured.
        Assert.Equal(JsonValueKind.Null, Entry(temp, "scratch").GetProperty("bytes").ValueKind);
        Assert.Equal(JsonValueKind.Number, Entry(temp, "private").GetProperty("bytes").ValueKind);

        var everythingInData = categories.Where(c => c.GetProperty("key").GetString() != "temp").Sum(Bytes);
        Assert.Equal(DiskUsage(data), everythingInData);
    }

    [Fact]
    public async Task An_unreadable_directory_marks_its_entry_incomplete_and_the_overview_still_answers()
    {
        using var dataRoot = new TempDir();
        await using var factory = FactoryOn(dataRoot.Path);
        var client = await factory.CreateAuthenticatedClientAsync();
        var alpha = await SeedRepositoryAsync(factory, "alpha");
        var a1 = Path.Combine(WorktreesRoot(factory), "a1");
        WriteFile(Path.Combine(a1, "own.bin"), 256 * KiB);
        var locked = Path.Combine(a1, "locked");
        WriteFile(Path.Combine(locked, "hidden.bin"), 256 * KiB);
        await SeedRunAsync(factory, "wi-a1", LoopRunStatus.Completed, a1, alpha);

        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            if (CanList(locked))
                Assert.Skip("This user can read a mode-000 directory (root), so nothing is unreadable.");

            var response = await client.GetAsync("/api/v1/storage", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var wt = Category(doc, "worktrees");
            Assert.True(RepositoryEntry(wt, alpha).GetProperty("incomplete").GetBoolean());
            Assert.True(wt.GetProperty("incomplete").GetBoolean());
            Assert.True(Bytes(RepositoryEntry(wt, alpha)) >= 256 * KiB);
            Assert.False(Category(doc, "chat-sessions").GetProperty("incomplete").GetBoolean());
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task A_dedicated_scratch_root_is_measured_as_its_own_temp_entry()
    {
        using var scratchRoot = new TempDir();
        var scratch = scratchRoot.Path;
        WriteFile(Path.Combine(scratch, "turn", "out.bin"), 1 * MiB);
        var previous = Environment.GetEnvironmentVariable(AgentIsolation.ScratchRootEnvVar);
        Environment.SetEnvironmentVariable(AgentIsolation.ScratchRootEnvVar, scratch);
        try
        {
            using var dataRoot = new TempDir();
            await using var factory = FactoryOn(dataRoot.Path);
            var client = await factory.CreateAuthenticatedClientAsync();

            using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/v1/storage", TestContext.Current.CancellationToken));

            var entry = doc.RootElement.GetProperty("categories").EnumerateArray()
                .Single(c => c.GetProperty("key").GetString() == "temp")
                .GetProperty("entries").EnumerateArray()
                .Single(e => e.GetProperty("key").GetString() == "scratch");
            AssertAbout(1 * MiB, Bytes(entry));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentIsolation.ScratchRootEnvVar, previous);
        }
    }

    private static ApiFactory FactoryOn(string dataRoot)
    {
        var factory = new ApiFactory();
        var previous = Environment.GetEnvironmentVariable("ILD_DATA_PATH");
        Environment.SetEnvironmentVariable("ILD_DATA_PATH", dataRoot);
        try { _ = factory.Services; }
        finally { Environment.SetEnvironmentVariable("ILD_DATA_PATH", previous); }
        return factory;
    }
}
