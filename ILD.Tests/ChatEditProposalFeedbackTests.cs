using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A chat that proposed a work item edit learns what the human decided on its
/// next turn: the decision rides that turn's prompt once it has reached the
/// agent, and again on the next turn if it never did. The decisions are read
/// from the WorkItem server, which being down must never cost the chat a turn.
/// </summary>
public sealed class ChatEditProposalFeedbackTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _scratchRoot = Path.Combine(Path.GetTempPath(), "ild-chat-proposal-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, true); } catch { }
    }

    [Fact]
    public async Task Each_decision_is_announced_on_the_next_turn_once_with_its_outcome_and_reason()
    {
        var adapter = new RecordingChatAdapter();
        var svc = NewService(adapter, NewWorkItemManager(_db.ServerOptions));
        var chat = (await svc.StartAsync("alice", (await SeedProviderAsync()).Id, new[] { "ild" })).Id;
        var otherChat = Guid.NewGuid();
        var opts = await _db.ServerOptions.ResolveForRepositoryAsync(null);

        var approvedItem = await CreateItemAsync("Approved item");
        var rejectedItem = await CreateItemAsync("Rejected item");
        var staleItem = await CreateItemAsync("Stale item");
        var pendingItem = await CreateItemAsync("Pending item");
        var approved = await ProposeAsync(approvedItem, chat);
        var rejected = await ProposeAsync(rejectedItem, chat);
        var goesStale = await ProposeAsync(staleItem, chat);
        var stillPending = await ProposeAsync(pendingItem, chat);
        var otherChats = await ProposeAsync(staleItem, otherChat);

        await svc.ExecuteTurnAsync(chat, Guid.NewGuid(), "turn one", CancellationToken.None);
        Assert.False(Mentions(adapter.Prompts[0], approved));

        Assert.Equal(EditProposalDecisionOutcome.Applied, (await _db.ServerClient.ApproveEditProposalAsync(opts, approvedItem, approved)).Outcome);
        Assert.Equal(EditProposalDecisionOutcome.Rejected, (await _db.ServerClient.RejectEditProposalAsync(opts, rejectedItem, rejected, "Too vague, name the failing test.")).Outcome);
        Assert.Equal(EditProposalDecisionOutcome.Applied, (await _db.ServerClient.ApproveEditProposalAsync(opts, staleItem, otherChats)).Outcome);

        await svc.ExecuteTurnAsync(chat, Guid.NewGuid(), "turn two", CancellationToken.None);
        var notice = adapter.Prompts[1];

        var approvedLine = LineNaming(notice, approved);
        Assert.Contains(approvedItem, approvedLine);
        Assert.Contains("Approved", approvedLine);
        var rejectedLine = LineNaming(notice, rejected);
        Assert.Contains(rejectedItem, rejectedLine);
        Assert.Contains("Rejected", rejectedLine);
        Assert.Contains("Too vague, name the failing test.", rejectedLine);
        var staleLine = LineNaming(notice, goesStale);
        Assert.Contains(staleItem, staleLine);
        Assert.Contains("Stale", staleLine);
        Assert.False(Mentions(notice, stillPending));
        Assert.False(Mentions(notice, otherChats));
        Assert.EndsWith("turn two", notice);

        await svc.ExecuteTurnAsync(chat, Guid.NewGuid(), "turn three", CancellationToken.None);
        foreach (var decided in new[] { approved, rejected, goesStale })
            Assert.False(Mentions(adapter.Prompts[2], decided));
    }

    [Fact]
    public async Task A_decision_whose_turn_never_reached_the_agent_is_announced_again()
    {
        var adapter = new RecordingChatAdapter();
        var svc = NewService(adapter, NewWorkItemManager(_db.ServerOptions));
        var chat = (await svc.StartAsync("alice", (await SeedProviderAsync()).Id, new[] { "ild" })).Id;
        var opts = await _db.ServerOptions.ResolveForRepositoryAsync(null);
        var item = await CreateItemAsync("Some item");
        var proposal = await ProposeAsync(item, chat);
        await svc.ExecuteTurnAsync(chat, Guid.NewGuid(), "turn one", CancellationToken.None);
        await _db.ServerClient.RejectEditProposalAsync(opts, item, proposal, "Not now.");

        adapter.FailNextTurnBeforeLaunch = true;
        await svc.ExecuteTurnAsync(chat, Guid.NewGuid(), "turn two", CancellationToken.None);
        Assert.True(Mentions(adapter.Prompts[1], proposal));

        await svc.ExecuteTurnAsync(chat, Guid.NewGuid(), "turn three", CancellationToken.None);
        Assert.Contains("Not now.", LineNaming(adapter.Prompts[2], proposal));

        await svc.ExecuteTurnAsync(chat, Guid.NewGuid(), "turn four", CancellationToken.None);
        Assert.False(Mentions(adapter.Prompts[3], proposal));
    }

    [Fact]
    public async Task An_unreachable_WorkItem_server_does_not_fail_the_turn()
    {
        var adapter = new RecordingChatAdapter();
        var unreachable = new WorkItemManager(
            Mock.Of<IRepositoryManager>(), _db.Providers, Mock.Of<IEventLogService>(), _db.LoopRuns,
            new WorkItemServerClient(new HttpClient(new RefusingHandler())), _db.ServerOptions);
        var svc = NewService(adapter, unreachable);
        var chat = (await svc.StartAsync("alice", (await SeedProviderAsync()).Id, new[] { "ild" })).Id;

        await svc.ExecuteTurnAsync(chat, Guid.NewGuid(), "hello", CancellationToken.None);

        Assert.EndsWith("hello", Assert.Single(adapter.Prompts));
        var reply = _db.Fresh().ChatMessages.Where(m => m.ChatSessionId == chat && m.Role == "assistant").ToList();
        Assert.Equal("ok", Assert.Single(reply).Content);
    }

    private static bool Mentions(string text, Guid proposalId)
        => text.Contains(proposalId.ToString(), StringComparison.OrdinalIgnoreCase);

    private static string LineNaming(string prompt, Guid proposalId)
    {
        var line = prompt.Split('\n').FirstOrDefault(l => Mentions(l, proposalId));
        Assert.True(line is not null, $"the prompt does not name proposal {proposalId}:\n{prompt}");
        return line!;
    }

    private async Task<string> CreateItemAsync(string title)
    {
        var opts = await _db.ServerOptions.ResolveForRepositoryAsync(null);
        return (await _db.ServerClient.CreateAsync(opts, new RemoteCreateWorkItemRequest { Title = title })).Id;
    }

    private async Task<Guid> ProposeAsync(string workItemId, Guid chatSessionId)
    {
        var opts = await _db.ServerOptions.ResolveForRepositoryAsync(null);
        var result = await _db.ServerClient.CreateEditProposalAsync(opts, workItemId, new RemoteCreateEditProposalRequest
        {
            Title = $"Proposed {Guid.NewGuid():N}",
            CreatedByChatSessionId = chatSessionId,
        });
        Assert.Equal(EditProposalCreateOutcome.Created, result.Outcome);
        return result.Proposal!.Id;
    }

    private WorkItemManager NewWorkItemManager(IWorkItemServerOptionsResolver options)
        => new(Mock.Of<IRepositoryManager>(), _db.Providers, Mock.Of<IEventLogService>(), _db.LoopRuns, _db.ServerClient, options);

    private ChatService NewService(IAgentAdapter adapter, IWorkItemManager workItems)
        => new(
            _db.Context,
            _db.Providers,
            Mock.Of<IAgentAdapterRegistry>(r =>
                r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => adapter)),
            Mock.Of<IChatNotifier>(),
            new ChatOptions { ScratchRoot = _scratchRoot },
            _db.LoopRuns,
            new ChatLoopScratchpad(),
            workItems: workItems);

    private async Task<AiProvider> SeedProviderAsync()
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "p1",
            Type = "claude-code",
            BaseUrl = "http://localhost",
            Model = "m",
            Parallelism = 1,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Context.AiProviders.Add(provider);
        await _db.Context.SaveChangesAsync();
        return provider;
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("Connection refused (workitem-server:8080)");
    }

    private sealed class RecordingChatAdapter : IAgentAdapter
    {
        private readonly List<AgentExecutionContext> _contexts = new();
        public List<string> Prompts => _contexts.Select(c => c.Prompt).ToList();

        /// <summary>
        /// Makes the next turn throw the way an adapter does when the CLI never
        /// launches — before any session is bound, so the prompt never reached an agent.
        /// </summary>
        public bool FailNextTurnBeforeLaunch { get; set; }

        public string Name => "fake";
        public string[] SupportedProviderTypes => ["fake"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            _contexts.Add(context);
            if (FailNextTurnBeforeLaunch)
            {
                FailNextTurnBeforeLaunch = false;
                throw new InvalidOperationException("claude: command not found");
            }
            return Task.FromResult(NodeExecutionResult.Ok("ok", context.Prompt, context.SessionId ?? "sess-1"));
        }
    }
}
