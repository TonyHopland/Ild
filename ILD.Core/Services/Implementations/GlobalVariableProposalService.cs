using System.Globalization;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;

namespace ILD.Core.Services.Implementations;

public sealed class GlobalVariableProposalService : IGlobalVariableProposalService
{
    private readonly IGlobalVariableProposalStore _proposals;
    private readonly ISchedulerSettingsService _settings;
    private readonly IAgentSessionResolver _sessions;
    private readonly IWorkItemNotifier _notifier;
    private readonly IChatNotifier _chatNotifier;

    public GlobalVariableProposalService(
        IGlobalVariableProposalStore proposals,
        ISchedulerSettingsService settings,
        IAgentSessionResolver sessions,
        IWorkItemNotifier notifier,
        IChatNotifier chatNotifier)
    {
        _proposals = proposals;
        _settings = settings;
        _sessions = sessions;
        _notifier = notifier;
        _chatNotifier = chatNotifier;
    }

    public async Task<GlobalVariableProposalResult> ProposeAsync(
        AgentGlobalVariableProposalRequest request, AgentProposer proposer, CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (!PromptPlaceholderRegistry.IsValidVariableName(name))
            return Invalid("A name is a letter followed by up to 127 letters, digits or underscores, e.g. review_rules.");
        if (request.Value is null)
            return Invalid("Suggest a value: the complete new text, which may be empty.");
        // Checked as they will be stored: the save drops NUL characters.
        var value = AppDbContext.WithoutNul(request.Value);
        var rationale = AppDbContext.WithoutNul(request.Rationale ?? string.Empty).Trim();
        if (rationale.Length == 0)
            return Invalid("Give a rationale: why the variable should say this.");
        var limit = await _settings.GetGlobalVariableMaxLengthAsync(ct);
        if (value.Length > limit)
            return Invalid(string.Create(CultureInfo.InvariantCulture,
                $"The value is {value.Length:N0} / {limit:N0} characters; consolidate it and suggest it again."));

        var proposal = new GlobalVariableProposal { Name = name, Value = value, Rationale = rationale };
        (await _sessions.StampAsync(proposer)).ApplyTo(proposal);
        proposal = await _proposals.ProposeAsync(proposal, ct);

        await _notifier.GlobalVariableProposalsChangedAsync(proposal.Name);
        if (proposer.ChatSessionId is { } chat)
            await _chatNotifier.GlobalVariableProposalsChangedAsync(chat);
        return new(GlobalVariableProposalOutcome.Created, proposal);
    }

    private static GlobalVariableProposalResult Invalid(string error) => new(GlobalVariableProposalOutcome.Invalid, Error: error);
}
