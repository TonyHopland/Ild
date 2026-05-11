using ILD.Core.Services.Implementations;
using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Renders a user-authored template against the current run state: collects
/// the event-log summary (best-effort), assembles the
/// <see cref="PromptContext"/>, and delegates to
/// <see cref="IPromptTemplateResolver"/>.
///
/// Previously this assembly was duplicated in <c>HumanNodeExecutor</c> and
/// <c>PRNodeExecutor</c> (and partly in <c>AINodeExecutor</c>): each fetched
/// the event log, built the context, and called the resolver — with subtle
/// differences in error handling. Centralising it gives all node types one
/// path for placeholder bugs and one place to evolve when new context fields
/// are added.
/// </summary>
public interface IPromptRenderingService
{
    /// <summary>
    /// Render <paramref name="template"/> for <paramref name="runId"/> using
    /// the supplied work-item state and previous-node output. Returns an
    /// empty string when the template is null or empty. Event-log lookup
    /// is best-effort; failure to fetch the log does not throw.
    /// </summary>
    Task<string> RenderAsync(
        string? template,
        Guid runId,
        WorkItemView workItem,
        string? previousNodeOutput);
}
