using System.Text.RegularExpressions;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using ILD.Tests.Fakes;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A chat that proposed a loop template change learns what the human decided
/// on its next turn: Applied (as is or edited, with the version) or Rejected
/// (with the reason) rides that turn's prompt once it has reached the agent,
/// and again on the next turn if it never did. A proposal that went Stale or
/// was superseded was decided by no human and is never announced, nor marked
/// delivered. Each turn runs on a service and context of its own, as each
/// request scope does.
/// </summary>
public sealed class ChatLoopTemplateProposalFeedbackTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _scratchRoot = Path.Combine(Path.GetTempPath(), "ild-chat-lt-proposal-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, true); } catch { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_human_decision_is_announced_once_on_the_next_turn_and_stale_or_superseded_ones_never()
    {
        var adapter = new RecordingChatAdapter();
        var chat = await StartChatAsync(adapter);
        var asIs = await ProposeAsync(chat, "AppliedAsIsLoop");
        var edited = await ProposeAsync(chat, "AppliedEditedLoop");
        var rejected = await ProposeAsync(chat, "RejectedLoop");
        var stale = await ProposeAsync(chat, "StaleLoop");
        var superseded = await ProposeAsync(chat, "SupersededLoop");
        await ProposeAsync(chat, "PendingLoop");
        var otherChats = await ProposeAsync(Guid.NewGuid(), "OtherChatsLoop");

        await TurnAsync(adapter, chat, "turn one");
        Assert.DoesNotContain("AppliedAsIsLoop", adapter.Prompts[0]);

        await DecideAsync(asIs, LoopTemplateProposalStatus.Applied, appliedVersion: 42);
        await DecideAsync(edited, LoopTemplateProposalStatus.Applied, appliedVersion: 57, editedBeforeSave: true);
        await DecideAsync(rejected, LoopTemplateProposalStatus.Rejected, reason: "Keep one build job.");
        await DecideAsync(stale, LoopTemplateProposalStatus.Stale);
        await DecideAsync(superseded, LoopTemplateProposalStatus.Superseded);
        await DecideAsync(otherChats, LoopTemplateProposalStatus.Applied, appliedVersion: 3);

        await TurnAsync(adapter, chat, "turn two");
        var notice = adapter.Prompts[1];

        var asIsLine = LineNaming(notice, asIs);
        Assert.Contains("Applied", asIsLine, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(StandaloneNumber(42), asIsLine);
        var editedLine = LineNaming(notice, edited);
        Assert.Contains("Applied", editedLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("edit", editedLine, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(StandaloneNumber(57), editedLine);
        var rejectedLine = LineNaming(notice, rejected);
        Assert.Contains("Rejected", rejectedLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Keep one build job.", rejectedLine);
        foreach (var (id, name) in new[] { stale, superseded, otherChats })
        {
            Assert.DoesNotContain(name, notice);
            Assert.DoesNotContain(id.ToString(), notice);
        }
        Assert.DoesNotContain("PendingLoop", notice);
        Assert.EndsWith("turn two", notice);

        await TurnAsync(adapter, chat, "turn three");
        foreach (var name in new[] { "AppliedAsIsLoop", "AppliedEditedLoop", "RejectedLoop", "StaleLoop" })
            Assert.DoesNotContain(name, adapter.Prompts[2]);

        await using var db = _db.Fresh();
        Assert.Null(await db.LoopTemplateProposals.Where(p => p.Id == stale.Id).Select(p => p.DecisionDeliveredAt).SingleAsync(Ct));
        Assert.Null(await db.LoopTemplateProposals.Where(p => p.Id == superseded.Id).Select(p => p.DecisionDeliveredAt).SingleAsync(Ct));
        Assert.NotNull(await db.LoopTemplateProposals.Where(p => p.Id == rejected.Id).Select(p => p.DecisionDeliveredAt).SingleAsync(Ct));
    }

    private static Regex StandaloneNumber(int n) => new($"(?<![0-9A-Za-z-]){n}(?![0-9A-Za-z-])");

    /// <summary>The prompt line about a proposal: it names the template, or the proposal itself.</summary>
    private static string LineNaming(string prompt, (Guid Id, string TemplateName) proposal)
    {
        var line = prompt.Split('\n').FirstOrDefault(l =>
            l.Contains(proposal.TemplateName, StringComparison.Ordinal) || l.Contains(proposal.Id.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.True(line is not null, $"the prompt does not name {proposal.TemplateName}:\n{prompt}");
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

    /// <summary>A pending proposal from the chat for a template of its own named <paramref name="templateName"/>.</summary>
    private async Task<(Guid Id, string TemplateName)> ProposeAsync(Guid chatSessionId, string templateName)
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
        return (proposal.Id, templateName);
    }

    private async Task DecideAsync((Guid Id, string TemplateName) proposal, LoopTemplateProposalStatus status,
        int? appliedVersion = null, bool editedBeforeSave = false, string? reason = null)
    {
        await using var db = _db.Fresh();
        var row = await db.LoopTemplateProposals.SingleAsync(p => p.Id == proposal.Id, Ct);
        row.Status = status;
        row.AppliedVersion = appliedVersion;
        row.EditedBeforeSave = editedBeforeSave;
        row.RejectionReason = reason;
        row.DecidedAt = DateTime.UtcNow;
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
            new ChatCloneCoordinator(), claudeProjects: TestClaudeProjectFolders.Instance);
}
