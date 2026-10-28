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
/// Settings &gt; Storage: who may call the storage endpoints.
/// </summary>
[SupportedOSPlatform("linux")]
public class StorageApiTests
{
    internal const long KiB = 1024;
    internal const long MiB = 1024 * KiB;

    // Directories and inode rounding the tests do not count by hand.
    internal const long Slack = 128 * KiB;

    [Fact]
    public async Task Every_storage_endpoint_is_for_the_user_only()
    {
        await using var factory = new ApiFactory();
        var anonymous = factory.CreateClient();
        var agent = factory.CreateClient();
        agent.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token);
        var ct = TestContext.Current.CancellationToken;

        foreach (var (caller, expected) in new[] { (anonymous, HttpStatusCode.Unauthorized), (agent, HttpStatusCode.Forbidden) })
        {
            Assert.Equal(expected, (await caller.GetAsync("/api/v1/storage", ct)).StatusCode);
        }

        // ...and not merely refused because nothing answers there.
        var user = await factory.CreateAuthenticatedClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/storage", ct)).StatusCode);
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

}

/// <summary>
/// Settings &gt; Storage: what the overview measures and where each byte is
/// attributed. Sizes are checked against real files on disk — allocated blocks,
/// hard links, sparse files and links — with <c>du</c>, which counts each inode
/// once and never follows a link, as the oracle for the whole data root.
/// The agent scratch root is set through a process-wide variable, hence the
/// collection.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
[SupportedOSPlatform("linux")]
// A host per test: each measures the host's own data root and the process-wide agent scratch root.
public class StorageOverviewTests
{
    [Fact]
    public async Task The_overview_counts_each_file_once_by_its_allocated_size_and_attributes_worktrees_through_their_runs()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var data = DataRoot(factory);
        Assert.Equal(factory.DataRoot, data);
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
            new[] { "agent-installs", "backups", "chat-sessions", "other", "preview-data", "repository-clones", "temp", "worktrees" },
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
    public async Task Temp_is_incomplete_when_the_scratch_root_is_not_measured()
    {
        var previous = Environment.GetEnvironmentVariable(AgentIsolation.ScratchRootEnvVar);
        Environment.SetEnvironmentVariable(AgentIsolation.ScratchRootEnvVar, null);
        try
        {
            await using var factory = new ApiFactory();
            var client = await factory.CreateAuthenticatedClientAsync();

            using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/v1/storage", TestContext.Current.CancellationToken));

            var temp = Category(doc, "temp");
            Assert.Equal(JsonValueKind.Null, Entry(temp, "scratch").GetProperty("bytes").ValueKind);
            Assert.True(temp.GetProperty("incomplete").GetBoolean());
            Assert.Equal(Entries(temp).Sum(e => e.GetProperty("bytes").ValueKind == JsonValueKind.Number ? Bytes(e) : 0), Bytes(temp));
            Assert.False(Category(doc, "chat-sessions").GetProperty("incomplete").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentIsolation.ScratchRootEnvVar, previous);
        }
    }

    [Fact]
    public async Task Preview_data_is_its_own_category_with_an_entry_per_repository_folder_and_orphans_marked()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var root = Path.Combine(WorktreesRoot(factory), ".preview-volumes");
        var kept = await SeedRepositoryAsync(factory, "kept-repo");
        var off = await SeedRepositoryAsync(factory, "off-repo");
        await PreviewDataApiTests.SetKeepAsync(factory, kept, keep: true);
        var deleted = Guid.NewGuid();
        WriteFile(Path.Combine(root, kept.ToString("D"), "data", "a.bin"), 1 * MiB);
        WriteFile(Path.Combine(root, off.ToString("D"), "data", "b.bin"), 512 * KiB);
        WriteFile(Path.Combine(root, deleted.ToString("D"), "cache", "c.bin"), 256 * KiB);
        WriteFile(Path.Combine(root, "stray", "d.bin"), 128 * KiB);

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/v1/storage", TestContext.Current.CancellationToken));

        var data = Category(doc, "preview-data");
        Assert.Equal("Preview data", data.GetProperty("label").GetString());
        var keptEntry = RepositoryEntry(data, kept);
        Assert.Equal("kept-repo", keptEntry.GetProperty("label").GetString());
        Assert.Equal("kept-repo", keptEntry.GetProperty("repositoryName").GetString());
        AssertAbout(1 * MiB, Bytes(keptEntry));
        var offEntry = RepositoryEntry(data, off);
        var offLabel = offEntry.GetProperty("label").GetString()!;
        Assert.Contains("off-repo", offLabel);
        Assert.NotEqual("off-repo", offLabel);
        Assert.Contains("off", offLabel.Replace("off-repo", ""), StringComparison.OrdinalIgnoreCase);
        AssertAbout(512 * KiB, Bytes(offEntry));
        var orphan = RepositoryEntry(data, deleted);
        Assert.Equal(JsonValueKind.Null, orphan.GetProperty("repositoryName").ValueKind);
        AssertAbout(256 * KiB, Bytes(orphan));
        var unowned = Entry(data, "unowned");
        Assert.Equal(JsonValueKind.Null, unowned.GetProperty("repositoryId").ValueKind);
        AssertAbout(128 * KiB, Bytes(unowned));
        Assert.Equal(Bytes(data), Entries(data).Sum(Bytes));

        Assert.True(Bytes(Entry(Category(doc, "worktrees"), "unowned")) < Slack, "preview data was also counted as unowned worktrees");
    }

    [Fact]
    public async Task An_unreadable_directory_marks_its_entry_incomplete_and_the_overview_still_answers()
    {
        await using var factory = new ApiFactory();
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
            await using var factory = new ApiFactory();
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
}
