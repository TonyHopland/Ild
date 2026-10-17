using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests.Integration;

/// <summary>
/// An API host for the agent proposal tests: the board and chat notifiers
/// recorded, runs reclaimable, <typeparamref name="TRace"/> intercepting every
/// statement, a signed-in human and agents calling the way the MCP server does.
/// </summary>
internal class ProposalApiTestHost<TRace> : IAsyncDisposable
    where TRace : DbCommandInterceptor, new()
{
    public const string RunHeader = "X-ILD-Run-Id";
    public const string ChatHeader = "X-ILD-Chat-Session-Id";

    public Mock<IWorkItemNotifier> WorkItemNotifier { get; } = new();
    public Mock<IChatNotifier> ChatNotifier { get; } = new();
    public TRace Race { get; } = new();
    public HttpClient Human { get; private set; } = null!;
    public Guid RepositoryId { get; private set; }
    public ApiFactory Factory { get; }

    private readonly string _hintName;

    /// <param name="hintName">What the notifier methods for this kind of proposal are named after.</param>
    public ProposalApiTestHost(string hintName)
    {
        _hintName = hintName;
        Factory = new ApiFactory(configureServices: services =>
        {
            services.ReplaceSingleton(WorkItemNotifier.Object);
            services.ReplaceSingleton(ChatNotifier.Object);
            var reclaimer = new Mock<IRunReclaimer>();
            reclaimer.Setup(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>())).ReturnsAsync(true);
            services.ReplaceSingleton(reclaimer.Object);
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(Race));
        });
    }

    public async Task StartAsync()
    {
        Human = await Factory.CreateAuthenticatedClientAsync();
        RepositoryId = await ProposalApiTestSupport.SeedRepositoryAsync(Factory);
    }

    public HttpClient Agent(Guid? runId = null, Guid? chatSessionId = null)
    {
        var client = Factory.CreateClient();
        var token = Factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (runId is { } run) client.DefaultRequestHeaders.Add(RunHeader, run.ToString());
        if (chatSessionId is { } chat) client.DefaultRequestHeaders.Add(ChatHeader, chat.ToString());
        return client;
    }

    public void ClearHints()
    {
        WorkItemNotifier.Invocations.Clear();
        ChatNotifier.Invocations.Clear();
    }

    /// <summary>The board was hinted that proposals changed, about <paramref name="target"/> when one is given.</summary>
    public bool BoardHinted(Guid? target = null)
        => WorkItemNotifier.Invocations.Any(i =>
            i.Method.Name.Contains(_hintName, StringComparison.Ordinal) && (target is null || i.Arguments.Contains(target.Value)));

    /// <summary>The chat was hinted that its proposals changed.</summary>
    public bool ChatHinted(Guid chatId)
        => ChatNotifier.Invocations.Any(i =>
            i.Method.Name.Contains(_hintName, StringComparison.Ordinal) && i.Arguments.Contains(chatId));

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}

/// <summary>Reading responses and seeding sources, for the agent proposal API tests.</summary>
public static class ProposalApiTestSupport
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public enum Caller { LoopRun, ChatSession }

    internal static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
        => JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct)).RootElement;

    internal static bool IsNullOrAbsent(JsonElement obj, string property)
        => !obj.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null;

    internal static string Status(JsonElement proposal) => proposal.GetProperty("status").GetString()!;

    /// <summary>The body of a 409 that gives a reason.</summary>
    internal static async Task<JsonElement> ConflictAsync(HttpResponseMessage resp, string because)
    {
        Assert.True(resp.StatusCode == HttpStatusCode.Conflict,
            $"{because}: expected 409, got {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        var body = await ReadJsonAsync(resp);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()), $"{because}: the 409 gives no reason");
        return body;
    }

    internal static async Task<(Guid? RunId, Guid? ChatId)> SeedCallerAsync<TRace>(ProposalApiTestHost<TRace> host, Caller caller)
        where TRace : DbCommandInterceptor, new()
        => caller == Caller.LoopRun
            ? (await SeedRunAsync(host.Factory), null)
            : (null, await SeedChatSessionAsync(host.Factory));

    internal static async Task<string> CreateHumanItemAsync<TRace>(ProposalApiTestHost<TRace> host)
        where TRace : DbCommandInterceptor, new()
    {
        var resp = await host.Human.PostAsJsonAsync("/api/v1/workitems", new
        {
            title = "An item",
            description = "",
            repositoryId = host.RepositoryId.ToString(),
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await ReadJsonAsync(resp)).GetProperty("id").GetString()!;
    }
    internal static async Task SetRunStatusAsync(ApiFactory factory, Guid runId, LoopRunStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.LoopRuns.SingleAsync(r => r.Id == runId, Ct);
        run.Status = status;
        run.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(Ct);
    }

    internal static async Task<bool> SourceExistsAsync(ApiFactory factory, Guid? runId, Guid? chatId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return runId is { } run
            ? await db.LoopRuns.AnyAsync(r => r.Id == run, Ct)
            : await db.ChatSessions.AnyAsync(c => c.Id == chatId, Ct);
    }

    internal static async Task SeedChatMessagesAsync(ApiFactory factory, Guid chatId, params string[] roles)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var i = 0; i < roles.Length; i++)
        {
            db.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(),
                ChatSessionId = chatId,
                Role = roles[i],
                Content = $"message {i}",
                Sequence = i,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync(Ct);
    }

    internal static async Task<(Guid RunId, Guid RunningNodeId)> SeedRunWithRunningStepAsync(ApiFactory factory, string workItemId)
    {
        var runId = await SeedRunAsync(factory, workItemId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var versionId = await db.LoopRuns.Where(r => r.Id == runId).Select(r => r.LoopTemplateVersionId).SingleAsync(Ct);
        var node = new LoopNode
        {
            Id = Guid.NewGuid(),
            LoopTemplateVersionId = versionId,
            NodeType = NodeType.AI,
            Label = "ai",
            CreatedAt = DateTime.UtcNow,
        };
        db.LoopNodes.Add(node);
        var step = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = runId,
            LoopNodeId = node.Id,
            Status = LoopRunNodeStatus.Running,
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        db.LoopRunNodes.Add(step);
        await db.SaveChangesAsync(Ct);
        return (runId, step.Id);
    }

    internal static async Task<Guid> SeedChatSessionAsync(ApiFactory factory, string owner = "proposal-tester")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = new ChatSession
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            AiProviderId = Guid.NewGuid(),
            ProviderType = "claude-code",
            ToolAllowlistCsv = "ild",
            ScratchPath = Path.Combine(Path.GetTempPath(), "ild-test-proposal-chat", Guid.NewGuid().ToString("N")),
            CreatedAt = DateTime.UtcNow,
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync(Ct);
        return session.Id;
    }

    /// <summary>A run of a template of its own, so the templates under test have no runs.</summary>
    internal static async Task<Guid> SeedRunAsync(ApiFactory factory, string? workItemId = null, LoopRunStatus status = LoopRunStatus.Running)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"run-host-{Guid.NewGuid():N}" };
        db.LoopTemplates.Add(template);
        var version = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = template.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.LoopTemplateVersions.Add(version);
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId ?? Guid.NewGuid().ToString(),
            LoopTemplateVersionId = version.Id,
            Status = status,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            StartedAt = DateTime.UtcNow,
        };
        db.LoopRuns.Add(run);
        await db.SaveChangesAsync(Ct);
        return run.Id;
    }

    internal static async Task<Guid> SeedRepositoryAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var provider = new RemoteProvider
        {
            Id = Guid.NewGuid(),
            Name = "test-provider",
            Type = "forgejo",
            Url = "https://example.invalid",
            CreatedAt = DateTime.UtcNow,
        };
        db.RemoteProviders.Add(provider);
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = "repo",
            CloneUrl = "https://example.invalid/repo.git",
            RemoteProviderId = provider.Id,
            DefaultIntakeStatus = WorkItemStatus.Backlog,
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(repo);
        await db.SaveChangesAsync(Ct);
        return repo.Id;
    }
}
