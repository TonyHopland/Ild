using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A chat that suggested a global variable change learns what the human decided
/// on its next turn: the decision rides that turn's prompt once it has reached
/// the agent, and again on the next turn if it never did. A suggestion its own
/// chat replaced was never decided by anyone and is never announced. Each turn
/// runs on a service and context of its own, as each request scope does.
/// </summary>
public sealed class ChatGlobalVariableProposalFeedbackTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _scratchRoot = Path.Combine(Path.GetTempPath(), "ild-chat-gv-proposal-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, true); } catch { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_decision_is_announced_on_the_next_turn_once_with_its_outcome_and_reason()
    {
        var adapter = new RecordingChatAdapter();
        var chat = await StartChatAsync(adapter);
        var otherChat = Guid.NewGuid();
        var approved = await SuggestAsync(chat, "approved_rules");
        var rejected = await SuggestAsync(chat, "rejected_rules");
        var goesStale = await SuggestAsync(chat, "stale_rules");
        var superseded = await SuggestAsync(chat, "superseded_rules");
        await SuggestAsync(chat, "pending_rules");
        var otherChats = await SuggestAsync(otherChat, "other_chats_rules");

        await TurnAsync(adapter, chat, "turn one");
        Assert.DoesNotContain("approved_rules", adapter.Prompts[0]);

        await DecideAsync(approved, GlobalVariableProposalStatus.Approved);
        await DecideAsync(rejected, GlobalVariableProposalStatus.Rejected, "Too vague, name the failing test.");
        await DecideAsync(goesStale, GlobalVariableProposalStatus.Stale);
        await DecideAsync(superseded, GlobalVariableProposalStatus.Superseded);
        await DecideAsync(otherChats, GlobalVariableProposalStatus.Approved);

        await TurnAsync(adapter, chat, "turn two");
        var notice = adapter.Prompts[1];

        Assert.Contains("Approved", LineNaming(notice, "approved_rules"), StringComparison.OrdinalIgnoreCase);
        var rejectedLine = LineNaming(notice, "rejected_rules");
        Assert.Contains("Rejected", rejectedLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Too vague, name the failing test.", rejectedLine);
        Assert.Contains("Stale", LineNaming(notice, "stale_rules"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("superseded_rules", notice);
        Assert.DoesNotContain("pending_rules", notice);
        Assert.DoesNotContain("other_chats_rules", notice);
        Assert.EndsWith("turn two", notice);

        await TurnAsync(adapter, chat, "turn three");
        foreach (var name in new[] { "approved_rules", "rejected_rules", "stale_rules" })
            Assert.DoesNotContain(name, adapter.Prompts[2]);
    }

    [Fact]
    public async Task A_decision_whose_turn_never_reached_the_agent_is_announced_again()
    {
        var adapter = new RecordingChatAdapter();
        var chat = await StartChatAsync(adapter);
        var suggestion = await SuggestAsync(chat, "review_rules");
        await TurnAsync(adapter, chat, "turn one");
        await DecideAsync(suggestion, GlobalVariableProposalStatus.Rejected, "Not now.");

        adapter.FailNextTurnBeforeLaunch = true;
        await TurnAsync(adapter, chat, "turn two");
        Assert.Contains("review_rules", adapter.Prompts[1]);

        await TurnAsync(adapter, chat, "turn three");
        Assert.Contains("Not now.", LineNaming(adapter.Prompts[2], "review_rules"));

        await TurnAsync(adapter, chat, "turn four");
        Assert.DoesNotContain("review_rules", adapter.Prompts[3]);
    }

    private static string LineNaming(string prompt, string name)
    {
        var line = prompt.Split('\n').FirstOrDefault(l => l.Contains(name, StringComparison.Ordinal));
        Assert.True(line is not null, $"the prompt does not name {name}:\n{prompt}");
        return line!;
    }

    private async Task<Guid> StartChatAsync(IAgentAdapter adapter)
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
        await using (var db = _db.Fresh())
        {
            db.AiProviders.Add(provider);
            await db.SaveChangesAsync(Ct);
        }
        await using var scope = _db.Fresh();
        return (await NewService(adapter, scope).StartAsync("alice", provider.Id, new[] { "ild" }, Ct)).Id;
    }

    private async Task TurnAsync(IAgentAdapter adapter, Guid chat, string message)
    {
        await using var scope = _db.Fresh();
        await NewService(adapter, scope).ExecuteTurnAsync(chat, Guid.NewGuid(), message, CancellationToken.None);
    }

    private async Task<Guid> SuggestAsync(Guid chatSessionId, string name)
    {
        await using var db = _db.Fresh();
        var proposal = new GlobalVariableProposal
        {
            Id = Guid.NewGuid(),
            Name = name,
            NormalizedName = GlobalVariable.Normalize(name),
            Value = $"Suggested text for {name}.",
            Rationale = "It reads better.",
            SnapshotUpdatedAt = null,
            CreatedByChatSessionId = chatSessionId,
            Status = GlobalVariableProposalStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        db.GlobalVariableProposals.Add(proposal);
        await db.SaveChangesAsync(Ct);
        return proposal.Id;
    }

    private async Task DecideAsync(Guid id, GlobalVariableProposalStatus status, string? reason = null)
    {
        await using var db = _db.Fresh();
        var proposal = await db.GlobalVariableProposals.SingleAsync(p => p.Id == id, Ct);
        proposal.Status = status;
        proposal.RejectionReason = reason;
        proposal.DecidedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(Ct);
    }

    private ChatService NewService(IAgentAdapter adapter, AppDbContext db)
        => new(
            db,
            _db.Providers,
            Mock.Of<IAgentAdapterRegistry>(r =>
                r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => adapter)).WithDeclaredTraits(),
            Mock.Of<IChatNotifier>(),
            new ChatOptions { ScratchRoot = _scratchRoot },
            _db.LoopRuns,
            new ChatLoopScratchpad(),
            new ChatCloneCoordinator());

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
        public AgentAdapterTraits Traits => AgentAdapterTraits.None;

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
