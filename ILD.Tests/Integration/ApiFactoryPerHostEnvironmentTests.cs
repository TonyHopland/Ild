using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Data;
using ILD.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// Two <see cref="ApiFactory"/> hosts that are both up at once each keep the
/// bootstrap credentials and attachment limits they were given. Both are built
/// before either is used, so a value one of them left in the process environment
/// would reach the other.
/// </summary>
public class ApiFactoryPerHostEnvironmentTests
{
    private const int Kilobyte = 1024;
    private const int Megabyte = 1024 * 1024;

    [Fact]
    public async Task Each_host_bootstraps_only_the_credentials_it_was_given()
    {
        await using var alice = new ApiFactory(environment: new Dictionary<string, string?>
        {
            ["ILD_USERNAME"] = "alice",
            ["ILD_PASSWORD"] = "alice-pw",
        });
        await using var bob = new ApiFactory(environment: new Dictionary<string, string?>
        {
            ["ILD_USERNAME"] = "bob",
            ["ILD_PASSWORD"] = "bob-pw",
        });
        var aliceClient = alice.CreateClient();
        var bobClient = bob.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await LoginAsync(aliceClient, "alice", "alice-pw"));
        Assert.Equal(HttpStatusCode.OK, await LoginAsync(bobClient, "bob", "bob-pw"));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(aliceClient, "bob", "bob-pw"));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(bobClient, "alice", "alice-pw"));
    }

    [Fact]
    public async Task Each_host_enforces_its_own_attachment_limits_in_the_api_and_the_work_item_server()
    {
        await using var tight = new ApiFactory(environment: new Dictionary<string, string?>
        {
            ["ILD_MAX_ATTACHMENT_MB"] = "1",
            ["ILD_MAX_ATTACHMENTS_TOTAL_MB"] = "1",
        });
        await using var roomy = new ApiFactory();
        var tightClient = await tight.CreateAuthenticatedClientAsync();
        var roomyClient = await roomy.CreateAuthenticatedClientAsync();
        var tightItem = await CreateWorkItemAsync(tight, tightClient);
        var roomyItem = await CreateWorkItemAsync(roomy, roomyClient);

        // Per file: refused by the API itself.
        Assert.Equal(HttpStatusCode.BadRequest, await UploadAsync(tightClient, tightItem, "big.bin", 2 * Megabyte));
        Assert.Equal(HttpStatusCode.Created, await UploadAsync(roomyClient, roomyItem, "big.bin", 2 * Megabyte));

        // Per item total: only the WorkItem server knows what the item already holds.
        Assert.Equal(HttpStatusCode.Created, await UploadAsync(tightClient, tightItem, "first.bin", 600 * Kilobyte));
        Assert.Equal(HttpStatusCode.BadRequest, await UploadAsync(tightClient, tightItem, "second.bin", 600 * Kilobyte));
        Assert.Equal(HttpStatusCode.Created, await UploadAsync(roomyClient, roomyItem, "first.bin", 600 * Kilobyte));
        Assert.Equal(HttpStatusCode.Created, await UploadAsync(roomyClient, roomyItem, "second.bin", 600 * Kilobyte));

        var tightLimits = await tightClient.GetFromJsonAsync<JsonElement>("/api/v1/settings/attachments", TestContext.Current.CancellationToken);
        var roomyLimits = await roomyClient.GetFromJsonAsync<JsonElement>("/api/v1/settings/attachments", TestContext.Current.CancellationToken);
        Assert.Equal(1L * Megabyte, tightLimits.GetProperty("maxBytesPerFile").GetInt64());
        Assert.Equal(1L * Megabyte, tightLimits.GetProperty("maxTotalBytesPerWorkItem").GetInt64());
        Assert.Equal(25L * Megabyte, roomyLimits.GetProperty("maxBytesPerFile").GetInt64());
        Assert.Equal(250L * Megabyte, roomyLimits.GetProperty("maxTotalBytesPerWorkItem").GetInt64());
    }

    private static async Task<HttpStatusCode> LoginAsync(HttpClient client, string username, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> UploadAsync(HttpClient client, string workItemId, string fileName, int size)
    {
        using var body = AttachmentUpload.Of(fileName, "application/octet-stream", AttachmentUpload.Bytes(size));
        using var response = await client.PostAsync($"/api/v1/workitems/{workItemId}/attachments", body);
        return response.StatusCode;
    }

    private static async Task<string> CreateWorkItemAsync(ApiFactory factory, HttpClient client)
    {
        var repositoryId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var provider = new RemoteProvider
            {
                Id = Guid.NewGuid(),
                Name = "per-host-provider",
                Type = "forgejo",
                Url = "https://example.invalid",
                CreatedAt = DateTime.UtcNow,
            };
            db.RemoteProviders.Add(provider);
            db.Repositories.Add(new Repository
            {
                Id = repositoryId,
                Name = "per-host-repo",
                CloneUrl = "https://example.invalid/repo.git",
                RemoteProviderId = provider.Id,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.PostAsJsonAsync("/api/v1/workitems", new
        {
            title = "per-host limits",
            description = "",
            repositoryId = repositoryId.ToString(),
        });
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }
}
