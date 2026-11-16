using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Moq;

namespace ILD.Tests;

/// <summary>An in-memory database seeded with the sessions an agent call can come from, and the resolver over it.</summary>
internal sealed class AgentSessionWorld : IDisposable
{
    public TestDb Db { get; } = new();
    public Mock<IWorkItemManager> WorkItems { get; } = new();
    public IAgentSessionResolver Resolver { get; }

    public AgentSessionWorld()
    {
        Resolver = AgentSessions.Resolver(Db, WorkItems.Object);
    }

    public void Dispose() => Db.Dispose();

    public static AgentCaller Run(Guid id) => Caller(AgentTokenSource.Run, id);

    public static AgentCaller Chat(Guid id) => Caller(AgentTokenSource.Chat, id);

    public static AgentCaller Grant(Guid id, ExternalAgentGrantScope scope = ExternalAgentGrantScope.Full)
        => new(false, true, "grant", id, scope.ToString(), null);

    public static AgentCaller Human(string name = "tester") => new(false, false, null, null, null, name);

    public static AgentCaller AgentWithoutSession() => new(true, false, null, null, null, null);

    private static AgentCaller Caller(AgentTokenSource source, Guid id)
        => new(true, false, source.ToString().ToLowerInvariant(), id, null, null);

    public WorkItemView WorkItem(string id, Guid? run = null, Guid? chat = null, Guid? grant = null)
    {
        var item = new WorkItemView
        {
            Id = id,
            CreatedByLoopRunId = run,
            CreatedByChatSessionId = chat,
            CreatedByExternalGrantId = grant,
        };
        WorkItems.Setup(m => m.GetWorkItemAsync(id)).ReturnsAsync(item);
        return item;
    }

    public async Task<LoopTemplate> SeedTemplateAsync(string name = "Release Loop", bool archived = false)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = name, IsArchived = archived };
        Db.Context.LoopTemplates.Add(template);
        Db.Context.LoopTemplateVersions.Add(new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 });
        await Db.Context.SaveChangesAsync();
        return template;
    }

    /// <summary>A running run on the work item; when <paramref name="stepRunning"/> it has a step still running, started before a finished one.</summary>
    public async Task<(Guid RunId, Guid? RunningNodeId)> SeedRunAsync(string? workItemId = null, bool stepRunning = false)
    {
        var template = await SeedTemplateAsync(Guid.NewGuid().ToString());
        var version = Db.Context.LoopTemplateVersions.Single(v => v.LoopTemplateId == template.Id);
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId ?? $"WI-{Guid.NewGuid():N}",
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
        };
        var node = new LoopNode
        {
            Id = Guid.NewGuid(),
            LoopTemplateVersionId = version.Id,
            NodeType = NodeType.AI,
            Label = "review",
            CreatedAt = DateTime.UtcNow,
        };
        var now = DateTime.UtcNow;
        var step = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = run.Id,
            LoopNodeId = node.Id,
            NodeLabel = "review",
            Status = stepRunning ? LoopRunNodeStatus.Running : LoopRunNodeStatus.Succeeded,
            StartedAt = now.AddMinutes(-2),
            CreatedAt = now.AddMinutes(-2),
        };
        var later = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = run.Id,
            LoopNodeId = node.Id,
            Status = LoopRunNodeStatus.Succeeded,
            StartedAt = now.AddMinutes(-1),
            CompletedAt = now,
            CreatedAt = now.AddMinutes(-1),
        };
        Db.Context.LoopRuns.Add(run);
        Db.Context.LoopNodes.Add(node);
        Db.Context.LoopRunNodes.AddRange(step, later);
        await Db.Context.SaveChangesAsync();
        return (run.Id, stepRunning ? step.Id : null);
    }

    /// <summary>A chat with one message per role, in order from sequence 0, inserted newest first.</summary>
    public async Task<Guid> SeedChatAsync(string userId = "chat-owner", params string[] roles)
    {
        var chat = new ChatSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AiProviderId = Guid.NewGuid(),
            ProviderType = "claude-code",
            ToolAllowlistCsv = "ild",
            ScratchPath = "/tmp/ild-test-chat-session",
            CreatedAt = DateTime.UtcNow,
        };
        Db.Context.ChatSessions.Add(chat);
        for (var i = roles.Length - 1; i >= 0; i--)
            Db.Context.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(),
                ChatSessionId = chat.Id,
                Role = roles[i],
                Content = $"message {i}",
                Sequence = i,
                CreatedAt = DateTime.UtcNow,
            });
        await Db.Context.SaveChangesAsync();
        return chat.Id;
    }

    public async Task<Guid> SeedGrantAsync(string name = "outside-agent")
    {
        var grant = new ExternalAgentGrant
        {
            Id = Guid.NewGuid(),
            Name = name,
            TokenHash = new string('a', 64),
            CreatedBy = "operator",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        };
        Db.Context.ExternalAgentGrants.Add(grant);
        await Db.Context.SaveChangesAsync();
        return grant.Id;
    }
}
