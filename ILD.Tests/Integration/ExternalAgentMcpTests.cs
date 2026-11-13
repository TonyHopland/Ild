using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ILD.Tests.Integration;

/// <summary>
/// An external MCP client reaches the in-process Streamable-HTTP endpoint with a grant token, and what it does
/// there goes through the agent API under that token.
/// </summary>
public class ExternalAgentMcpTests(ExternalAgentMcpTests.Host host) : IClassFixture<ExternalAgentMcpTests.Host>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed class Host : IAsyncLifetime
    {
        public ApiFactory Factory { get; } = new();
        public HttpClient Operator { get; private set; } = null!;
        public Guid RepositoryId { get; private set; }

        public async ValueTask InitializeAsync()
        {
            Operator = await Factory.CreateAuthenticatedClientAsync();
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var provider = new RemoteProvider { Id = Guid.NewGuid(), Name = "mcp-provider", Type = "forgejo", Url = "https://example.invalid", CreatedAt = DateTime.UtcNow };
            var repository = new Repository
            {
                Id = Guid.NewGuid(),
                Name = "repo",
                CloneUrl = "https://example.invalid/repo.git",
                RemoteProviderId = provider.Id,
                DefaultIntakeStatus = WorkItemStatus.Backlog,
                CreatedAt = DateTime.UtcNow,
            };
            db.AddRange(provider, repository);
            await db.SaveChangesAsync();
            RepositoryId = repository.Id;
        }

        public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
    }

    private async Task<(Guid Id, string Token)> CreateGrantAsync(string scope = "Full")
    {
        var response = await host.Operator.PostAsJsonAsync("/api/v1/external-grants", new { name = "mcp-client", lifetimeMinutes = 60, scope }, Ct);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        return (json.GetProperty("grant").GetProperty("id").GetGuid(), json.GetProperty("token").GetString()!);
    }

    private async Task<McpClient> ConnectAsync(string token)
    {
        var http = host.Factory.CreateClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        }, http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static string TextOf(CallToolResult result)
        => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    [Fact]
    public async Task The_endpoint_refuses_a_caller_with_no_token_or_a_dead_one()
    {
        var anonymous = await host.Factory.CreateClient().PostAsync("/mcp", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var (id, token) = await CreateGrantAsync();
        await host.Operator.PostAsync($"/api/v1/external-grants/{id}/revoke", null, Ct);
        var revoked = host.Factory.CreateClient();
        revoked.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await revoked.PostAsync("/mcp", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_endpoint_is_not_answered_with_the_SPA_for_a_path_below_it()
    {
        var response = await host.Factory.CreateClient().GetAsync("/mcp/unknown", Ct);

        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task It_offers_the_tools_that_need_no_run_or_chat_and_none_that_do()
    {
        var (_, token) = await CreateGrantAsync();
        await using var client = await ConnectAsync(token);

        var names = (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToHashSet();

        Assert.Contains("list_workitems", names);
        Assert.Contains("create_workitem", names);
        Assert.Contains("propose_workitem_edit", names);
        Assert.Contains("get_loop_authoring_guide", names);
        Assert.DoesNotContain("get_loop_variables", names);
        Assert.DoesNotContain("set_loop_variable", names);
        Assert.DoesNotContain("get_current_loop", names);
        Assert.DoesNotContain("update_current_loop", names);
        Assert.DoesNotContain("clone_repository", names);
    }

    [Fact]
    public async Task A_client_lists_creates_and_proposes_and_the_proposal_waits_for_the_operator()
    {
        var (grantId, token) = await CreateGrantAsync();
        var existing = await host.Operator.PostAsJsonAsync("/api/v1/workitems",
            new { title = "existing", description = "old", repositoryId = host.RepositoryId.ToString() }, Ct);
        var existingId = JsonDocument.Parse(await existing.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetString()!;
        await using var client = await ConnectAsync(token);

        var repositories = await client.CallToolAsync("list_repositories", cancellationToken: Ct);
        Assert.Contains(host.RepositoryId.ToString(), TextOf(repositories));

        var created = await client.CallToolAsync("create_workitem", new Dictionary<string, object?>
        {
            ["title"] = "made over mcp",
            ["description"] = "from outside",
            ["repositoryId"] = host.RepositoryId.ToString(),
        }, cancellationToken: Ct);
        Assert.NotEqual(true, created.IsError);
        var createdId = JsonDocument.Parse(TextOf(created)).RootElement.GetProperty("id").GetString()!;

        var read = await client.CallToolAsync("get_workitem", new Dictionary<string, object?> { ["id"] = createdId }, cancellationToken: Ct);
        Assert.Contains("made over mcp", TextOf(read));
        var item = JsonDocument.Parse(await host.Operator.GetStringAsync($"/api/v1/workitems/{createdId}", Ct)).RootElement;
        Assert.Equal(grantId, item.GetProperty("createdByExternalGrantId").GetGuid());

        var proposed = await client.CallToolAsync("propose_workitem_edit", new Dictionary<string, object?>
        {
            ["id"] = existingId,
            ["description"] = "new",
            ["rationale"] = "clearer",
        }, cancellationToken: Ct);
        Assert.NotEqual(true, proposed.IsError);

        var detail = JsonDocument.Parse(await host.Operator.GetStringAsync($"/api/v1/external-grants/{grantId}", Ct)).RootElement;
        Assert.Equal(existingId, Assert.Single(detail.GetProperty("pendingProposals").EnumerateArray()).GetProperty("target").GetString());
        var paths = detail.GetProperty("activity").EnumerateArray().Select(a => a.GetProperty("path").GetString()).ToList();
        Assert.Contains("/mcp", paths);
        Assert.Contains("/api/v1/agent/workitems", paths);
    }

    [Fact]
    public async Task A_tool_call_under_a_read_only_grant_is_refused_by_the_api()
    {
        var (_, token) = await CreateGrantAsync("ReadOnly");
        await using var client = await ConnectAsync(token);

        var result = await client.CallToolAsync("create_workitem", new Dictionary<string, object?>
        {
            ["title"] = "nope",
            ["description"] = "",
            ["repositoryId"] = host.RepositoryId.ToString(),
        }, cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("read-only", TextOf(result));
    }
}
