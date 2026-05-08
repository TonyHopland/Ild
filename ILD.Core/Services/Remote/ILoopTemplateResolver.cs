namespace ILD.Core.Services.Remote;

/// <summary>
/// Resolves a remote work item's tags to a local loop template that can
/// execute it. Per PRD §3.7: tag name must match a template name. No match
/// or multiple matches is escalated to HumanFeedback by the coordinator.
/// </summary>
public interface ILoopTemplateResolver
{
    /// <summary>
    /// Returns the resolution outcome for the given tag set.
    /// </summary>
    LoopTemplateResolution Resolve(IReadOnlyList<string> tags);
}

public enum LoopTemplateResolutionKind
{
    None,
    Single,
    Ambiguous,
}

public sealed record LoopTemplateResolution(
    LoopTemplateResolutionKind Kind,
    Guid? TemplateId,
    IReadOnlyList<string> MatchingTemplateNames);
