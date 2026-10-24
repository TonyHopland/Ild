using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The exact briefing a chat's agent gets about the proposals a human decided
/// since its last turn: edit proposals, then global variable suggestions, then
/// loop template proposals, each block with its header and intro, one line per
/// decision ordered by when it was decided, then the context preamble and the
/// user's message. Which decisions are told is per kind: a suggestion's Stale is,
/// a loop template proposal's is not, and neither kind's Superseded or Pending.
/// </summary>
public sealed class ChatProposalDecisionBriefingTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _scratchRoot = Path.Combine(Path.GetTempPath(), "ild-chat-briefing-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, true); } catch { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_decided_proposals_of_every_kind_are_briefed_word_for_word_once_before_the_preamble_and_message()
    {
        var adapter = new RecordingChatAdapter();
        var chat = await StartChatAsync(adapter);

        var opts = await _db.ServerOptions.ResolveForRepositoryAsync(null, Ct);
        var item = (await _db.ServerClient.CreateAsync(opts, new RemoteCreateWorkItemRequest { Title = "Flaky build" }, TestActors.Human, Ct)).Id;
        var edit = await _db.ServerClient.CreateEditProposalAsync(opts, item, new RemoteCreateEditProposalRequest
        {
            Title = "Fix the flaky build",
            CreatedByChatSessionId = chat,
        }, TestActors.Human, Ct);

        var gvApproved = await SuggestAsync(chat, "review_rules");
        var gvRejected = await SuggestAsync(chat, "build_rules");
        var gvUnexplained = await SuggestAsync(chat, "lint_rules");
        var gvStale = await SuggestAsync(chat, "deploy_rules");
        var gvSuperseded = await SuggestAsync(chat, "superseded_rules");
        await SuggestAsync(chat, "pending_rules");

        var ltAsIs = await ProposeLoopAsync(chat, "Dev Loop");
        var ltEdited = await ProposeLoopAsync(chat, "Docs Loop");
        var ltUnexplained = await ProposeLoopAsync(chat, "Release Loop");
        var ltDeleted = await ProposeLoopAsync(chat, "Gone Loop");
        var ltStale = await ProposeLoopAsync(chat, "Stale Loop");
        var ltSuperseded = await ProposeLoopAsync(chat, "Superseded Loop");
        await ProposeLoopAsync(chat, "Pending Loop");

        await TurnAsync(adapter, chat, "turn one");

        await _db.ServerClient.RejectEditProposalAsync(opts, item, edit.Proposal!.Id, "Name   the\nfailing test.", TestActors.Human, Ct);
        // Decided out of insertion order: the briefing follows DecidedAt.
        await DecideSuggestionAsync(gvStale, GlobalVariableProposalStatus.Stale, T0.AddMinutes(1));
        await DecideSuggestionAsync(gvRejected, GlobalVariableProposalStatus.Rejected, T0.AddMinutes(2), "Too vague.\n\n  Name the\tfailing test.");
        await DecideSuggestionAsync(gvApproved, GlobalVariableProposalStatus.Approved, T0.AddMinutes(3));
        await DecideSuggestionAsync(gvUnexplained, GlobalVariableProposalStatus.Rejected, T0.AddMinutes(4));
        await DecideSuggestionAsync(gvSuperseded, GlobalVariableProposalStatus.Superseded, T0.AddMinutes(5));
        await DecideLoopAsync(ltEdited, LoopTemplateProposalStatus.Applied, T0.AddMinutes(1), appliedVersion: 57, editedBeforeSave: true);
        await DecideLoopAsync(ltUnexplained, LoopTemplateProposalStatus.Rejected, T0.AddMinutes(2));
        await DecideLoopAsync(ltAsIs, LoopTemplateProposalStatus.Applied, T0.AddMinutes(3), appliedVersion: 42);
        await DecideLoopAsync(ltDeleted, LoopTemplateProposalStatus.Rejected, T0.AddMinutes(4), reason: "Keep one\r\nbuild job.");
        await DecideLoopAsync(ltStale, LoopTemplateProposalStatus.Stale, T0.AddMinutes(5));
        await DecideLoopAsync(ltSuperseded, LoopTemplateProposalStatus.Superseded, T0.AddMinutes(6));
        await DeleteTemplateAsync(ltDeleted.TemplateId);

        await TurnAsync(adapter, chat, "turn two");
        await TurnAsync(adapter, chat, "turn three");

        var briefing = string.Join("\n",
            "[Edit proposal decisions]",
            "A human decided these work item edit proposals you made in this chat since you last heard:",
            $"- Proposal {edit.Proposal.Id} on work item {item}: Rejected. Reason: Name the failing test.",
            "",
            "[Global variable suggestion decisions]",
            "A human decided these global variable suggestions you made in this chat since you last heard:",
            $"- Suggestion {gvStale} for deploy_rules: Stale; the variable changed before it was approved, so nothing was applied. "
                + "Read it again with get_global_variables and suggest again if the change still makes sense.",
            $"- Suggestion {gvRejected} for build_rules: Rejected. Reason: Too vague. Name the failing test.",
            $"- Suggestion {gvApproved} for review_rules: Approved; the variable now holds your suggested text.",
            $"- Suggestion {gvUnexplained} for lint_rules: Rejected, with no reason given.",
            "",
            "[Loop template proposal decisions]",
            "A human decided these loop template changes you proposed in this chat since you last heard:",
            $"- Proposal {ltEdited.Id} for Docs Loop: Applied as version 57, after the human edited it in the Loop Editor; read it with get_loop_template to see what was saved.",
            $"- Proposal {ltUnexplained.Id} for Release Loop: Rejected, with no reason given.",
            $"- Proposal {ltAsIs.Id} for Dev Loop: Applied as version 42, exactly as you proposed it.",
            $"- Proposal {ltDeleted.Id} for a template since deleted: Rejected. Reason: Keep one build job.");

        // Turn three is told nothing, so it is the preamble and message alone.
        var told = adapter.Prompts[1];
        var untold = adapter.Prompts[2];
        Assert.EndsWith("turn three", untold);
        Assert.Equal(briefing + "\n\n" + untold[..^"turn three".Length] + "turn two", told);

        await using var db = _db.Fresh();
        Assert.NotNull(await DeliveredAtAsync(db.GlobalVariableProposals, gvStale));
        Assert.Null(await DeliveredAtAsync(db.GlobalVariableProposals, gvSuperseded));
        Assert.NotNull(await DeliveredAtAsync(db.LoopTemplateProposals, ltDeleted.Id));
        Assert.Null(await DeliveredAtAsync(db.LoopTemplateProposals, ltStale.Id));
        Assert.Null(await DeliveredAtAsync(db.LoopTemplateProposals, ltSuperseded.Id));
    }

    [Fact]
    public async Task Decisions_of_both_local_kinds_whose_turn_never_reached_the_agent_are_briefed_again_and_marked_only_then()
    {
        var adapter = new RecordingChatAdapter();
        var chat = await StartChatAsync(adapter);
        var suggestion = await SuggestAsync(chat, "review_rules");
        var loop = await ProposeLoopAsync(chat, "Dev Loop");
        await TurnAsync(adapter, chat, "turn one");
        await DecideSuggestionAsync(suggestion, GlobalVariableProposalStatus.Rejected, T0, "Not now.");
        await DecideLoopAsync(loop, LoopTemplateProposalStatus.Applied, T0, appliedVersion: 2);

        adapter.FailNextTurnBeforeLaunch = true;
        await TurnAsync(adapter, chat, "turn two");
        await using (var db = _db.Fresh())
        {
            Assert.Null(await DeliveredAtAsync(db.GlobalVariableProposals, suggestion));
            Assert.Null(await DeliveredAtAsync(db.LoopTemplateProposals, loop.Id));
        }

        await TurnAsync(adapter, chat, "turn three");
        await TurnAsync(adapter, chat, "turn four");

        var suggestionLine = $"- Suggestion {suggestion} for review_rules: Rejected. Reason: Not now.";
        var loopLine = $"- Proposal {loop.Id} for Dev Loop: Applied as version 2, exactly as you proposed it.";
        foreach (var prompt in adapter.Prompts.Skip(1).Take(2))
        {
            Assert.Contains(suggestionLine, prompt.Split('\n'));
            Assert.Contains(loopLine, prompt.Split('\n'));
        }
        Assert.DoesNotContain(suggestion.ToString(), adapter.Prompts[3]);
        Assert.DoesNotContain(loop.Id.ToString(), adapter.Prompts[3]);
        await using (var db = _db.Fresh())
        {
            Assert.NotNull(await DeliveredAtAsync(db.GlobalVariableProposals, suggestion));
            Assert.NotNull(await DeliveredAtAsync(db.LoopTemplateProposals, loop.Id));
        }
    }

    private static Task<DateTime?> DeliveredAtAsync(IQueryable<GlobalVariableProposal> set, Guid id)
        => set.Where(p => p.Id == id).Select(p => p.DecisionDeliveredAt).SingleAsync(Ct);

    private static Task<DateTime?> DeliveredAtAsync(IQueryable<LoopTemplateProposal> set, Guid id)
        => set.Where(p => p.Id == id).Select(p => p.DecisionDeliveredAt).SingleAsync(Ct);

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
            CreatedByChatSessionId = chatSessionId,
            Status = GlobalVariableProposalStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        db.GlobalVariableProposals.Add(proposal);
        await db.SaveChangesAsync(Ct);
        return proposal.Id;
    }

    private async Task DecideSuggestionAsync(Guid id, GlobalVariableProposalStatus status, DateTime decidedAt, string? reason = null)
    {
        await using var db = _db.Fresh();
        var proposal = await db.GlobalVariableProposals.SingleAsync(p => p.Id == id, Ct);
        proposal.Status = status;
        proposal.RejectionReason = reason;
        proposal.DecidedAt = decidedAt;
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>A pending proposal from the chat for a template of its own named <paramref name="templateName"/>.</summary>
    private async Task<(Guid Id, Guid TemplateId)> ProposeLoopAsync(Guid chatSessionId, string templateName)
    {
        await using var db = _db.Fresh();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = templateName, Description = "", CreatedAt = DateTime.UtcNow };
        db.LoopTemplates.Add(template);
        db.LoopTemplateVersions.Add(new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow });
        var proposal = new LoopTemplateProposal
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = template.Id,
            BaseVersion = 1,
            Document = $$"""{"$schema":"ild-loop-template/v2","name":"{{templateName}}","description":"","recoveryPolicy":"AutoResume","nodes":[],"edges":[]}""",
            Rationale = "Builds keep failing.",
            CreatedByChatSessionId = chatSessionId,
            Status = LoopTemplateProposalStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        db.LoopTemplateProposals.Add(proposal);
        await db.SaveChangesAsync(Ct);
        return (proposal.Id, template.Id);
    }

    private async Task DecideLoopAsync((Guid Id, Guid TemplateId) proposal, LoopTemplateProposalStatus status, DateTime decidedAt,
        int? appliedVersion = null, bool editedBeforeSave = false, string? reason = null)
    {
        await using var db = _db.Fresh();
        var row = await db.LoopTemplateProposals.SingleAsync(p => p.Id == proposal.Id, Ct);
        row.Status = status;
        row.AppliedVersion = appliedVersion;
        row.EditedBeforeSave = editedBeforeSave;
        row.RejectionReason = reason;
        row.DecidedAt = decidedAt;
        await db.SaveChangesAsync(Ct);
    }

    private async Task DeleteTemplateAsync(Guid templateId)
    {
        await using var db = _db.Fresh();
        db.LoopTemplateVersions.RemoveRange(db.LoopTemplateVersions.Where(v => v.LoopTemplateId == templateId));
        db.LoopTemplates.Remove(await db.LoopTemplates.SingleAsync(t => t.Id == templateId, Ct));
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
            new ChatCloneCoordinator(),
            workItems: new WorkItemManager(
                Mock.Of<IRepositoryManager>(), _db.Providers, Mock.Of<IEventLogService>(), _db.LoopRuns, _db.ServerClient, _db.ServerOptions, TestActors.Instance), claudeProjects: TestClaudeProjectFolders.Instance);

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
