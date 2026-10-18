using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static ILD.Tests.Integration.StorageApiTests;

namespace ILD.Tests.Integration;

/// <summary>
/// A keep-on repository's persisted preview data over the API: one work item's
/// preview holds it at a time, and only a person removes it — by the repository's
/// Reset, through the one delete endpoint the storage page uses — never while a
/// preview holds it.
/// </summary>
[SupportedOSPlatform("linux")]
public class PreviewDataApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Refusal(string workItemId, string title)
        => $"A preview is already running for work item {workItemId} ({title}). Stop it before starting this one.";

    private static string HolderSentence(string workItemId, string title)
        => $"A preview is already running for work item {workItemId} ({title}).";

    [Fact]
    public async Task One_work_items_preview_holds_the_repositorys_data_against_other_starts_and_every_removal_until_it_stops()
    {
        // The preview service the API runs, from the same container as the delete.
        await using var factory = new ApiFactory(configureServices: services =>
        {
            services.RemoveAll<IWorktreePreviewService>();
            services.AddSingleton<IWorktreePreviewService, WorktreePreviewService>();
        });
        var user = await factory.CreateAuthenticatedClientAsync();
        var agent = AgentClient(factory);
        var repositoryId = await SeedRepositoryAsync(factory, "alpha");
        await SetKeepAsync(factory, repositoryId, keep: true);
        var (a, aTitle, _) = await PreviewableWorkItemAsync(factory, user, repositoryId, "First item", "wt-a");
        var (b, bTitle, bWorktree) = await PreviewableWorkItemAsync(factory, user, repositoryId, "Second item", "wt-b");
        var folder = PreviewDataFolder(factory, repositoryId);

        Assert.Equal(HttpStatusCode.OK, (await user.PostAsJsonAsync($"/api/v1/workitems/{a}/preview/start", new { }, Ct)).StatusCode);
        Assert.True(File.Exists(Path.Combine(folder, "data", "file")));

        foreach (var (client, path) in new[]
        {
            (user, $"/api/v1/workitems/{b}/preview/start"),
            (agent, $"/api/v1/agent/workitems/{b}/preview/start"),
            (user, $"/api/v1/workitems/{b}/preview/services/web/start"),
            (agent, $"/api/v1/agent/workitems/{b}/preview/services/web/start"),
        })
        {
            var refused = await client.PostAsJsonAsync(path, new { }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal(Refusal(a, aTitle), await ErrorAsync(refused));
        }
        Assert.False(File.Exists(Path.Combine(bWorktree, "listing.txt")), "a refused start ran an install step");

        await AssertRemovalRefusedAsync(user, repositoryId, HolderSentence(a, aTitle));
        Assert.True(File.Exists(Path.Combine(folder, "data", "file")));

        Assert.Equal(HttpStatusCode.OK, (await user.PostAsync($"/api/v1/workitems/{a}/preview/stop", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.DeleteAsync($"/api/v1/storage/preview-data/{repositoryId}", Ct)).StatusCode);
        Assert.False(AgentWritableFiles.EntryExists(folder));

        // The next preview starts on empty volumes.
        Assert.Equal(HttpStatusCode.OK, (await user.PostAsJsonAsync($"/api/v1/workitems/{b}/preview/start", new { }, Ct)).StatusCode);
        Assert.Equal("", File.ReadAllText(Path.Combine(bWorktree, "listing.txt")));
        Assert.Equal(HttpStatusCode.OK, (await user.PostAsync($"/api/v1/workitems/{b}/preview/stop", null, Ct)).StatusCode);

        // A keep-off preview holds the repository too.
        await SetKeepAsync(factory, repositoryId, keep: false);
        Assert.Equal(HttpStatusCode.OK, (await user.PostAsJsonAsync($"/api/v1/workitems/{b}/preview/start", new { }, Ct)).StatusCode);
        Directory.CreateDirectory(Path.Combine(folder, "data"));
        File.WriteAllText(Path.Combine(folder, "data", "left"), "x");
        await AssertRemovalRefusedAsync(user, repositoryId, HolderSentence(b, bTitle));
        Assert.True(File.Exists(Path.Combine(folder, "data", "left")));

        Assert.Equal(HttpStatusCode.OK, (await user.PostAsync($"/api/v1/workitems/{b}/preview/stop", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.DeleteAsync($"/api/v1/storage/preview-data/{repositoryId}", Ct)).StatusCode);
        Assert.False(AgentWritableFiles.EntryExists(folder));
    }

    [Fact]
    public async Task Deleting_preview_data_is_for_the_user_and_refuses_ids_that_are_not_guids()
    {
        await using var factory = new ApiFactory();
        var user = await factory.CreateAuthenticatedClientAsync();
        var agent = AgentClient(factory);
        var repositoryId = await SeedRepositoryAsync(factory, "alpha");
        await SetKeepAsync(factory, repositoryId, keep: true);
        var folder = PreviewDataFolder(factory, repositoryId);
        WriteFile(Path.Combine(folder, "data", "file.bin"), 4 * KiB);
        var deleted = Guid.NewGuid();
        var orphan = PreviewDataFolder(factory, deleted);
        WriteFile(Path.Combine(orphan, "data", "file.bin"), 4 * KiB);

        Assert.Equal(HttpStatusCode.Forbidden, (await agent.DeleteAsync($"/api/v1/storage/preview-data/{repositoryId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.DeleteAsync($"/api/v1/storage/preview-data/{deleted}", Ct)).StatusCode);
        Assert.True(File.Exists(Path.Combine(folder, "data", "file.bin")));
        Assert.True(File.Exists(Path.Combine(orphan, "data", "file.bin")));

        Assert.Equal(HttpStatusCode.BadRequest, (await user.DeleteAsync("/api/v1/storage/preview-data/not-a-guid", Ct)).StatusCode);
        Assert.True(File.Exists(Path.Combine(orphan, "data", "file.bin")));

        // Also the folder of a deleted repository.
        Assert.Equal(HttpStatusCode.OK, (await user.DeleteAsync($"/api/v1/storage/preview-data/{deleted}", Ct)).StatusCode);
        Assert.False(AgentWritableFiles.EntryExists(orphan));

        Assert.Equal(HttpStatusCode.OK, (await user.DeleteAsync($"/api/v1/storage/preview-data/{repositoryId}", Ct)).StatusCode);
        Assert.False(AgentWritableFiles.EntryExists(folder));
    }

    [Fact]
    public async Task Turning_the_setting_off_or_deleting_the_repository_leaves_its_preview_data_in_place()
    {
        await using var factory = new ApiFactory();
        var user = await factory.CreateAuthenticatedClientAsync();
        var repositoryId = await SeedRepositoryAsync(factory, "alpha");
        await SetKeepAsync(factory, repositoryId, keep: true);
        var file = Path.Combine(PreviewDataFolder(factory, repositoryId), "data", "file.bin");
        WriteFile(file, 4 * KiB);
        Guid providerId;
        using (var scope = factory.Services.CreateScope())
            providerId = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Repositories.AsNoTracking()
                .SingleAsync(r => r.Id == repositoryId, Ct)).RemoteProviderId;

        var off = await user.PutAsJsonAsync($"/api/v1/repositories/{repositoryId}", new
        {
            name = "alpha", cloneUrl = "https://example.com/alpha.git", defaultBranch = "main",
            remoteProviderId = providerId.ToString(), defaultIntakeStatus = "Backlog", keepPreviewData = false,
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.True(File.Exists(file));

        Assert.True((await user.DeleteAsync($"/api/v1/repositories/{repositoryId}", Ct)).IsSuccessStatusCode);
        Assert.True(File.Exists(file));
    }

    private static async Task AssertRemovalRefusedAsync(HttpClient user, Guid repositoryId, string holderSentence)
    {
        var removal = await user.DeleteAsync($"/api/v1/storage/preview-data/{repositoryId}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, removal.StatusCode);
        Assert.StartsWith(holderSentence, await ErrorAsync(removal));
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.GetProperty("error").GetString();
    }

    private static HttpClient AgentClient(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token);
        return client;
    }

    internal static string PreviewDataFolder(ApiFactory factory, Guid repositoryId)
        => Path.Combine(WorktreesRoot(factory), ".preview-volumes", repositoryId.ToString("D"));

    internal static async Task SetKeepAsync(ApiFactory factory, Guid repositoryId, bool keep)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Repositories.SingleAsync(r => r.Id == repositoryId)).KeepPreviewData = keep;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A work item of the repository whose current run has a worktree with one
    /// volume: the install lists what is already in it, then writes a file there.
    /// </summary>
    private static async Task<(string Id, string Title, string Worktree)> PreviewableWorkItemAsync(
        ApiFactory factory, HttpClient user, Guid repositoryId, string title, string worktreeName)
    {
        var created = await user.PostAsJsonAsync("/api/v1/workitems", new { title, description = "", repositoryId = repositoryId.ToString() }, Ct);
        created.EnsureSuccessStatusCode();
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetString()!;

        var worktree = Path.Combine(WorktreesRoot(factory), worktreeName);
        Directory.CreateDirectory(worktree);
        var config = new
        {
            preview = new
            {
                profiles = new
                {
                    app = new
                    {
                        volumes = new[] { "data" },
                        install = new[]
                        {
                            new { command = "ls -A \"${VOLUME:data}\" > \"${WORKTREE}/listing.txt\"" },
                            new { command = "printf x > \"${VOLUME:data}/file\"" },
                        },
                        services = new[]
                        {
                            new
                            {
                                name = "web",
                                port = "web",
                                command = "exec node -e \"require('http').createServer((q,r)=>r.end('ok')).listen(process.env.PORT)\"",
                                env = new Dictionary<string, string> { ["PORT"] = "${PORT}" },
                                healthUrl = "http://127.0.0.1:${PORT}/",
                            },
                        },
                    },
                },
            },
        };
        File.WriteAllText(Path.Combine(worktree, "ild.config.json"), JsonSerializer.Serialize(config));
        await SeedRunAsync(factory, id, LoopRunStatus.WaitingHuman, worktree, repositoryId);
        return (id, title, worktree);
    }
}
