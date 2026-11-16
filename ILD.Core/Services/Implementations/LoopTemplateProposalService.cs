using System.Text.Json;
using System.Text.Json.Serialization;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;

namespace ILD.Core.Services.Implementations;

public sealed class LoopTemplateProposalService : ILoopTemplateProposalService
{
    /// <summary>
    /// Upper bound on a pushed loop document (~1 MB). A real ild-loop-template/v2 is a few KB; this stops a rogue
    /// agent from shoving megabytes over the chat hub.
    /// </summary>
    public const int MaxLoopDocumentChars = 1_000_000;

    /// <summary>
    /// The Edit List as stored: the edits as the server parsed them, re-serialised without null members. Not the
    /// document format, so not <see cref="LoopTemplateDocument.Render()"/>.
    /// </summary>
    private static readonly JsonSerializerOptions EditListJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILoopTemplateProposalStore _proposals;
    private readonly ILoopTemplateManager _templates;
    private readonly ILoopTemplateLookup _lookup;
    private readonly IAgentSessionResolver _sessions;
    private readonly IWorkItemNotifier _notifier;
    private readonly IChatNotifier _chatNotifier;

    public LoopTemplateProposalService(
        ILoopTemplateProposalStore proposals,
        ILoopTemplateManager templates,
        ILoopTemplateLookup lookup,
        IAgentSessionResolver sessions,
        IWorkItemNotifier notifier,
        IChatNotifier chatNotifier)
    {
        _proposals = proposals;
        _templates = templates;
        _lookup = lookup;
        _sessions = sessions;
        _notifier = notifier;
        _chatNotifier = chatNotifier;
    }

    public async Task<LoopTemplateProposalResult> ProposeAsync(
        AgentLoopTemplateProposalRequest request, AgentProposer proposer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.NameOrId))
            return Invalid("nameOrId is required: the loop template's id or name, as get_loop_template takes it.");
        if (request.BaseVersion is not { } baseVersion || baseVersion < 1)
            return Invalid("baseVersion is required: the version get_loop_template returned with the document, 1 or greater.");
        string? edits = null;
        if (request.Edits is not null)
        {
            if (request.Document is not null)
                return Invalid("Send either document or edits, not both: edits for most changes, a whole document only for a rewrite.");
            if (request.Edits.Count == 0)
                return Invalid("edits is empty: send at least one edit, or a whole document.");
            edits = JsonSerializer.Serialize(request.Edits, EditListJson);
            if (edits.Length > MaxLoopDocumentChars)
                return TooLarge("edits", edits.Length);
        }
        else if (string.IsNullOrWhiteSpace(request.Document))
            return Invalid("document or edits is required: an Edit List to apply to the latest version, or the complete ild-loop-template/v2 document, as JSON text.");
        else if (request.Document.Length > MaxLoopDocumentChars)
            return TooLarge("document", request.Document.Length);
        // Checked as it will be stored: the save drops NUL characters.
        var rationale = AppDbContext.WithoutNul(request.Rationale ?? string.Empty).Trim();
        if (rationale.Length == 0)
            return Invalid("Give a rationale: why the loop should change this way.");
        if (rationale.Length > LoopTemplateProposal.MaxRationaleLength)
            return Invalid($"The rationale is {rationale.Length} characters; keep it to {LoopTemplateProposal.MaxRationaleLength}.");

        var found = await _lookup.FindAsync(request.NameOrId);
        if (found.Template is not { } template)
            return found.Outcome == LoopTemplateLookupOutcome.Ambiguous
                ? new(LoopTemplateProposalOutcome.AmbiguousName, Error: found.Error, Candidates: found.Candidates)
                : new(LoopTemplateProposalOutcome.TemplateNotFound, Error: found.Error);

        string document;
        if (request.Edits is null)
            document = request.Document!;
        else
        {
            var latest = await _templates.GetLatestDocumentAsync(template);
            if (latest is not { Version: var latestVersion, Document: var latestDocument } || latestVersion != baseVersion)
                return NotLatest(template, baseVersion, latest?.Version ?? 0);

            var applied = LoopDocumentEditor.ApplyEditList(latestDocument, request.Edits);
            if (applied.Failure is { } failure)
                return new(LoopTemplateProposalOutcome.Invalid, Error: EditListFailureMessage(failure), EditIndex: failure.Index);
            document = applied.Document!;
            if (document.Length > MaxLoopDocumentChars)
                return TooLarge("document", document.Length);
        }

        var proposed = _templates.CheckProposedDocument(document);
        if (proposed.Error is { } documentError)
            return Invalid(documentError);
        if (proposed.ValidationErrors.Count > 0)
            return new(LoopTemplateProposalOutcome.Invalid,
                Error: "A save would refuse this loop. Fix every error listed and propose again.",
                ValidationErrors: proposed.ValidationErrors);

        var proposal = new LoopTemplateProposal
        {
            LoopTemplateId = template.Id,
            BaseVersion = baseVersion,
            Document = proposed.Document!,
            Edits = edits,
            Rationale = rationale,
        };
        (await _sessions.StampAsync(proposer)).ApplyTo(proposal);
        var result = await _proposals.ProposeAsync(proposal, ct);

        switch (result.Outcome)
        {
            case LoopTemplateProposeOutcome.TemplateNotFound:
                return new(LoopTemplateProposalOutcome.TemplateNotFound, Error: $"Loop template not found: {request.NameOrId.Trim()}");
            case LoopTemplateProposeOutcome.NotLatest:
                return NotLatest(template, baseVersion, result.LatestVersion);
        }

        await LoopTemplateProposalHints.SendAsync(
            _notifier, _chatNotifier, result.Proposal!.LoopTemplateId, 1, proposer.ChatSessionId is { } chat ? [chat] : []);
        return new(LoopTemplateProposalOutcome.Created, result.Proposal);
    }

    private static LoopTemplateProposalResult Invalid(string error) => new(LoopTemplateProposalOutcome.Invalid, Error: error);

    private static LoopTemplateProposalResult TooLarge(string member, int length)
        => Invalid($"{member} is too large ({length} chars); the limit is {MaxLoopDocumentChars}.");

    private static LoopTemplateProposalResult NotLatest(LoopTemplate template, int baseVersion, int? latestVersion)
        => new(LoopTemplateProposalOutcome.NotLatest,
            Error: $"Version {baseVersion} is not the latest version of loop template '{template.Name}'; the latest is version {latestVersion}. "
                + "Read it with get_loop_template, make your change to that document and propose again.",
            LatestVersion: latestVersion);

    private static string EditListFailureMessage(LoopEditListFailure failure)
    {
        var names = new[]
        {
            failure.Op,
            failure.NodeId is null ? null : $"node '{failure.NodeId}'",
            failure.Field is null ? null : $"field '{failure.Field}'",
        }.OfType<string>().ToList();
        var named = names.Count == 0 ? string.Empty : $" ({string.Join(", ", names)})";
        return $"edits[{failure.Index}]{named}: {failure.Message}";
    }
}
