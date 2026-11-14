using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// An agent token names the run or chat it was issued for, and the agent API takes the caller from it: nothing the
/// agent sends can make it another run, and a run's token reaches only its own work item and the items it created.
/// </summary>
public class AgentTokenIsolationTests(SharedApiFactory factory) : IClassFixture<SharedApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpRequestMessage Request(HttpMethod method, string url, string token, object? body = null, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<HttpStatusCode> SendAsync(HttpRequestMessage request)
        => (await factory.CreateClient().SendAsync(request, Ct)).StatusCode;

    private async Task<Guid> SeedRunAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "token-template" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow };
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid().ToString(),
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            StartedAt = DateTime.UtcNow,
        };
        db.AddRange(template, version, run);
        await db.SaveChangesAsync(Ct);
        return run.Id;
    }

    private async Task<Guid> SeedRepositoryAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var provider = new RemoteProvider { Id = Guid.NewGuid(), Name = "token-provider", Type = "forgejo", Url = "https://example.invalid", CreatedAt = DateTime.UtcNow };
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
        await db.SaveChangesAsync(Ct);
        return repository.Id;
    }

    private async Task<string> CreateWorkItemAsync(string token)
    {
        var response = await factory.CreateClient().SendAsync(Request(
            HttpMethod.Post, "/api/v1/agent/workitems", token,
            new { title = "item", description = "", repositoryId = (await SeedRepositoryAsync()).ToString() }), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> VariablesOfAsync(string token)
    {
        var response = await factory.CreateClient().SendAsync(Request(HttpMethod.Get, "/api/v1/agent/variables", token), Ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private Task<HttpStatusCode> SetVariableAsync(string token, string value, params (string Name, string Value)[] headers)
        => SendAsync(Request(HttpMethod.Put, "/api/v1/agent/variables/handoff", token, new { value }, headers));

    [Fact]
    public async Task A_run_writes_its_own_variables_and_a_header_naming_another_run_changes_nothing()
    {
        var runA = await SeedRunAsync();
        var runB = await SeedRunAsync();
        var tokenA = factory.Services.IssueAgentToken(AgentTokenSource.Run, runA);
        var tokenB = factory.Services.IssueAgentToken(AgentTokenSource.Run, runB);

        Assert.Equal(HttpStatusCode.OK, await SetVariableAsync(tokenB, "b-value"));
        Assert.Equal(HttpStatusCode.Forbidden, await SetVariableAsync(tokenA, "overwritten", ("X-ILD-Run-Id", runB.ToString())));
        Assert.Equal(HttpStatusCode.OK, await SetVariableAsync(tokenA, "a-value"));
        Assert.Equal(HttpStatusCode.OK, await SetVariableAsync(tokenA, "a-again", ("X-ILD-Run-Id", runA.ToString())));

        Assert.Equal("b-value", (await VariablesOfAsync(tokenB))[0].GetProperty("value").GetString());
        Assert.Equal("a-again", (await VariablesOfAsync(tokenA))[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task A_run_token_cannot_reach_a_work_item_of_another_run()
    {
        var runA = await SeedRunAsync();
        var runB = await SeedRunAsync();
        var tokenA = factory.Services.IssueAgentToken(AgentTokenSource.Run, runA);
        var tokenB = factory.Services.IssueAgentToken(AgentTokenSource.Run, runB);
        var itemOfB = await CreateWorkItemAsync(tokenB);
        var itemOfA = await CreateWorkItemAsync(tokenA);
        var attachment = Guid.NewGuid();

        var refused = new[]
        {
            Request(HttpMethod.Put, $"/api/v1/agent/workitems/{itemOfB}/preview/services/web/config", tokenA, new { }),
            Request(HttpMethod.Get, $"/api/v1/agent/workitems/{itemOfB}/preview/services/web/config", tokenA),
            Request(HttpMethod.Post, $"/api/v1/agent/workitems/{itemOfB}/preview/start", tokenA, new { }),
            Request(HttpMethod.Post, $"/api/v1/agent/workitems/{itemOfB}/preview/stop", tokenA),
            Request(HttpMethod.Get, $"/api/v1/agent/workitems/{itemOfB}/preview/logs?service=web", tokenA),
            Request(HttpMethod.Get, $"/api/v1/agent/workitems/{itemOfB}?includeConversation=true", tokenA),
            Request(HttpMethod.Get, $"/api/v1/agent/workitems/{itemOfB}/attachments/{attachment}", tokenA),
            Request(HttpMethod.Get, $"/api/v1/agent/workitems/{itemOfB}/pr-review", tokenA),
            Request(HttpMethod.Post, $"/api/v1/agent/workitems/{itemOfB}/pull-branch", tokenA),
            Request(HttpMethod.Get, $"/api/v1/agent/workitems/{itemOfB}/ci-log?checkId=1", tokenA),
            Request(HttpMethod.Get, $"/api/v1/agent/workitems/{itemOfB}/worktree/status", tokenA),
            Request(HttpMethod.Get, $"/api/v1/agent/workitems/{itemOfB}/worktree/diff", tokenA),
            Request(HttpMethod.Get, $"/api/v1/agent/workitems/{itemOfB}/worktree/file?path=a.txt", tokenA),
            Request(HttpMethod.Get, $"/api/v1/agent/workitems/{itemOfB}/worktree/search?pattern=a", tokenA),
            Request(HttpMethod.Get, $"/api/v1/agent/workitems/{itemOfB}/worktree/log", tokenA),
            Request(HttpMethod.Get, $"/api/v1/agent/workitems/{itemOfB}/worktree/commits/abcdef1", tokenA),
        };
        foreach (var request in refused)
            Assert.True(HttpStatusCode.Forbidden == await SendAsync(request), $"{request.Method} {request.RequestUri} was not refused");

        Assert.Equal(HttpStatusCode.OK, await SendAsync(
            Request(HttpMethod.Get, $"/api/v1/agent/workitems/{itemOfA}?includeConversation=true", tokenA)));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(
            Request(HttpMethod.Get, $"/api/v1/agent/workitems/{itemOfB}", tokenA)));
    }

    [Fact]
    public async Task A_spoofed_header_cannot_turn_a_run_token_into_another_run_or_a_chat()
    {
        var runA = await SeedRunAsync();
        var tokenA = factory.Services.IssueAgentToken(AgentTokenSource.Run, runA);
        var repository = (await SeedRepositoryAsync()).ToString();

        foreach (var (name, value) in new[] { ("X-ILD-Run-Id", Guid.NewGuid().ToString()), ("X-ILD-Chat-Session-Id", Guid.NewGuid().ToString()), ("X-ILD-Run-Id", "not-a-guid") })
            Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(
                Request(HttpMethod.Post, "/api/v1/agent/workitems", tokenA, new { title = "t", description = "", repositoryId = repository }, (name, value))));

        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(Request(
            HttpMethod.Post, "/api/v1/agent/workitems", tokenA,
            new { title = "t", description = "", repositoryId = repository, createdByLoopRunId = Guid.NewGuid().ToString() })));
    }

    [Fact]
    public async Task A_chat_token_reaches_any_work_item_but_only_as_its_own_chat()
    {
        var runB = await SeedRunAsync();
        var chat = Guid.NewGuid();
        var chatToken = factory.Services.IssueAgentToken(AgentTokenSource.Chat, chat);
        var itemOfB = await CreateWorkItemAsync(factory.Services.IssueAgentToken(AgentTokenSource.Run, runB));

        Assert.Equal(HttpStatusCode.OK, await SendAsync(
            Request(HttpMethod.Get, $"/api/v1/agent/workitems/{itemOfB}?includeConversation=true", chatToken)));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(
            Request(HttpMethod.Get, $"/api/v1/agent/workitems/{itemOfB}?includeConversation=true", chatToken, headers: ("X-ILD-Chat-Session-Id", chat.ToString()))));

        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(
            Request(HttpMethod.Get, $"/api/v1/agent/workitems/{itemOfB}", chatToken, headers: ("X-ILD-Run-Id", runB.ToString()))));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(
            Request(HttpMethod.Get, $"/api/v1/agent/workitems/{itemOfB}", chatToken, headers: ("X-ILD-Chat-Session-Id", Guid.NewGuid().ToString()))));
        Assert.Equal(HttpStatusCode.BadRequest, await SetVariableAsync(chatToken, "x"));
    }

    [Fact]
    public async Task A_revoked_token_is_refused_and_the_other_launches_of_the_run_are_untouched()
    {
        var run = await SeedRunAsync();
        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IAgentTokenService>();
        var first = await tokens.IssueAsync(AgentTokenSource.Run, run, ct: Ct);
        var second = await tokens.IssueAsync(AgentTokenSource.Run, run, ct: Ct);

        Assert.Equal(HttpStatusCode.OK, await SendAsync(Request(HttpMethod.Get, "/api/v1/agent/variables", first.Token)));

        await tokens.RevokeAsync(first.TokenId, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(Request(HttpMethod.Get, "/api/v1/agent/variables", first.Token)));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(Request(HttpMethod.Get, "/api/v1/agent/variables", second.Token)));

        await tokens.RevokeAllForAsync(AgentTokenSource.Run, run, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(Request(HttpMethod.Get, "/api/v1/agent/variables", second.Token)));
    }

    [Fact]
    public async Task An_expired_token_and_an_unknown_one_are_refused()
    {
        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IAgentTokenService>();
        var expired = await tokens.IssueAsync(AgentTokenSource.Chat, Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-1), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(Request(HttpMethod.Get, "/api/v1/agent/variables", expired.Token)));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(Request(HttpMethod.Get, "/api/v1/agent/variables", "not-a-token")));
    }

    [Fact]
    public async Task Only_the_hash_of_a_token_is_stored()
    {
        var token = factory.Services.IssueAgentToken(AgentTokenSource.Chat, Guid.NewGuid());

        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AgentTokens.AsNoTracking()
            .Select(t => t.TokenHash).ToListAsync(Ct);

        Assert.DoesNotContain(token, stored);
        Assert.Contains(SessionTokenHasher.Hash(token), stored);
    }
}
