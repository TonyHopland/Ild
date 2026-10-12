using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// <c>post_pr_review_finding</c> as an agent reaches it: a malformed finding is
/// a bad request, and a well-formed one on a work item with no live pull
/// request is refused with a reason, as every other queuing tool is.
/// </summary>
public class AgentPrReviewFindingApiTests
{
    private static async Task<string> SeedWorkItemAsync(ApiFactory factory, HttpClient client)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var provider = new RemoteProvider
            {
                Id = Guid.NewGuid(), Name = "p", Type = "forgejo", Url = "https://example.invalid", CreatedAt = DateTime.UtcNow,
            };
            db.RemoteProviders.Add(provider);
            db.Repositories.Add(new Repository
            {
                Id = Guid.NewGuid(), Name = "repo", CloneUrl = "https://example.invalid/repo.git",
                RemoteProviderId = provider.Id, DefaultIntakeStatus = WorkItemStatus.Backlog, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        Guid repoId;
        using (var scope = factory.Services.CreateScope())
            repoId = scope.ServiceProvider.GetRequiredService<AppDbContext>().Repositories.First().Id;

        var created = await client.PostAsJsonAsync("/api/v1/agent/workitems", new
        {
            title = "reviewed item",
            description = "",
            repositoryId = repoId.ToString(),
        });
        created.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    [Theory]
    [InlineData("src/A.cs", 12, "blocker", "This leaks the file handle.")]
    [InlineData("src/A.cs", 12, "", "This leaks the file handle.")]
    [InlineData("src/A.cs", 12, null, "This leaks the file handle.")]
    [InlineData("", 12, "important", "This leaks the file handle.")]
    [InlineData("   ", 12, "nit", "This leaks the file handle.")]
    [InlineData("src/A.cs", 0, "important", "This leaks the file handle.")]
    [InlineData("src/A.cs", -3, "nit", "This leaks the file handle.")]
    [InlineData("src/A.cs", 12, "important", "")]
    [InlineData("src/A.cs", 12, "nit", "  ")]
    public async Task A_malformed_finding_is_a_bad_request(string path, int line, string? severity, string body)
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var workItemId = await SeedWorkItemAsync(factory, client);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/agent/workitems/{workItemId}/pr-review/finding",
            new { path, line, severity, body },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_finding_too_long_to_post_with_its_heading_and_marker_is_a_bad_request()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var workItemId = await SeedWorkItemAsync(factory, client);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/agent/workitems/{workItemId}/pr-review/finding",
            new { path = "src/A.cs", line = 12, severity = "nit", body = new string('x', 65001) },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("important")]
    [InlineData("nit")]
    [InlineData("Important")]
    [InlineData(" NIT ")]
    public async Task A_finding_on_an_item_with_no_live_pull_request_is_refused_with_a_reason(string severity)
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var workItemId = await SeedWorkItemAsync(factory, client);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/agent/workitems/{workItemId}/pr-review/finding",
            new { path = "src/A.cs", line = 12, severity, body = "This leaks the file handle." },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("message").GetString()));
    }
}
