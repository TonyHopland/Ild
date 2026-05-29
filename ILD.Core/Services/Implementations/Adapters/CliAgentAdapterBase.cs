using System.Diagnostics;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// Shared scaffolding for CLI-backed <see cref="IAgentAdapter"/> implementations
/// (claude-code, opencode, pi). These adapters all render the same prompt
/// template, persist/restore managed sessions through the same snapshot store,
/// and parse JSON event streams the same way; this base owns those identical
/// pieces so they live in exactly one place. The per-CLI parts — how the
/// process is launched and how its output is parsed/finalized — stay in the
/// concrete adapter.
/// </summary>
public abstract class CliAgentAdapterBase : IAgentAdapter
{
    private static readonly IPromptTemplateResolver Resolver = new PromptTemplateResolver();

    /// <summary>Null when constructed without DI (e.g. unit tests); session snapshot helpers no-op in that case.</summary>
    protected IServiceScopeFactory? ScopeFactory { get; }

    protected CliAgentAdapterBase()
    {
    }

    protected CliAgentAdapterBase(IServiceScopeFactory scopeFactory)
    {
        ScopeFactory = scopeFactory;
    }

    public abstract string Name { get; }
    public abstract string[] SupportedProviderTypes { get; }
    public virtual ConfigFieldDescriptor[] ConfigSchema => Array.Empty<ConfigFieldDescriptor>();

    public abstract Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context);

    /// <summary>Render an AI-node prompt template against the run's placeholder context.</summary>
    protected static Task<string> RenderPromptAsync(string template, LoopRunContext context)
        => Task.FromResult(Resolver.Render(template, new PromptContext(
            WorkItemTitle: context.WorkItemTitle,
            WorkItemDescription: context.WorkItemDescription,
            PreviousNodeOutput: context.PreviousNodeOutput,
            EventLogSummary: context.EventLogSummary,
            WorktreePath: context.WorktreePath)));

    /// <summary>Best-effort kill of a process and its children; never throws.</summary>
    protected static void KillProcessTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Fetch the managed-session snapshot for this adapter, keyed by run + this
    /// adapter's <see cref="Name"/>. Returns <c>null</c> when no DI scope is
    /// available (unit tests) or no snapshot exists. Store exceptions propagate
    /// to the caller, matching the previous per-adapter behavior.
    /// </summary>
    protected async Task<AdapterSessionSnapshot?> GetSnapshotAsync(Guid loopRunId, string sessionId, CancellationToken ct)
    {
        if (ScopeFactory is null) return null;
        await using var scope = ScopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAdapterSessionSnapshotStore>();
        return await store.GetAsync(loopRunId, Name, sessionId, ct);
    }

    /// <summary>Persist a managed-session snapshot for this adapter. No-ops without a DI scope.</summary>
    protected async Task UpsertSnapshotAsync(Guid loopRunId, string sessionId, string sessionJson, CancellationToken ct)
    {
        if (ScopeFactory is null) return;
        await using var scope = ScopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAdapterSessionSnapshotStore>();
        await store.UpsertAsync(loopRunId, Name, sessionId, sessionJson, ct);
    }

    /// <summary>Try to read a string property from a JSON object element.</summary>
    protected static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Read a string property from a JSON object element, or <c>null</c> if absent/non-string.</summary>
    protected static string? GetString(JsonElement element, string propertyName)
        => TryGetString(element, propertyName, out var value) ? value : null;
}
