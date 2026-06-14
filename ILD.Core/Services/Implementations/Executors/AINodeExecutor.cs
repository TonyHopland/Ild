using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace ILD.Core.Services.Implementations.Executors;

public sealed class AINodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.AI;

    public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Ai>(ctx.Node.Config);
        var sp = ctx.Services;
        var providerStore = sp.GetRequiredService<IProviderStore>();
        var workItems = sp.GetRequiredService<IWorkItemManager>();
        var registry = sp.GetService<IAgentAdapterRegistry>();
        var concurrency = sp.GetService<IAiProviderConcurrencyTracker>();
        var rendering = sp.GetService<IPromptRenderingService>();

        var wi = await workItems.GetWorkItemAsync(ctx.Run.WorkItemId);
        if (wi is null)
        {
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "WorkItem not found");
            yield break;
        }

        AiProvider? provider;
        if (Guid.TryParse(cfg.AiProviderId, out var parsedId))
        {
            provider = await providerStore.GetAiProviderByIdAsync(parsedId);
            if (provider is null)
            {
                yield return new NodeOutcome.Fail(EdgeType.OnFailure, $"AiProvider {parsedId} not found");
                yield break;
            }
        }
        else
        {
            provider = await providerStore.GetDefaultAiProviderAsync();
            if (provider is null)
            {
                yield return new NodeOutcome.Fail(EdgeType.OnFailure, "AI node has no aiProviderId and no default provider is configured");
                yield break;
            }
        }

        if (registry is null)
        {
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "No agent adapter registry");
            yield break;
        }
        IAgentAdapter? adapter = null;
        string? adapterError = null;
        try { adapter = registry.ResolveForProvider(provider)(); }
        catch (Exception ex) { adapterError = ex.Message; }
        if (adapter is null)
        {
            yield return new NodeOutcome.Fail(
                EdgeType.OnFailure,
                adapterError is null
                    ? $"No adapter for provider type '{provider.Type}'"
                    : $"Could not resolve adapter for provider type '{provider.Type}': {adapterError}");
            yield break;
        }

        var providerId = provider.Id;
        if (concurrency is not null && !concurrency.TryEnter(providerId, provider.Parallelism))
        {
            yield return new NodeOutcome.WaitingIld($"AI provider '{provider.Name}' at capacity");
            yield break;
        }

        // The session-id capture and note-clear writes run in their own DI
        // scopes: the first fires on the adapter's stream task (concurrent with
        // the engine's scope), the second must survive the engine's pre-routing
        // reload. Both touch a single column to avoid clobbering control-plane
        // writes (halt, pause, cancel) on the same run.
        var scopeFactory = sp.GetService<IServiceScopeFactory>();

        // A halt→resume parks a one-shot steering note on the run. When present
        // it overrides the node config: continue the SAME captured AI session
        // (ignore UseSession) with the human's note — or a neutral continue when
        // they gave none — as the next message. The note is cleared as it is
        // consumed so a later visit to this node runs normally.
        var steeringNote = ctx.Run.SteeringNote;
        var isSteering = steeringNote is not null;

        var prompt = isSteering
            ? (string.IsNullOrWhiteSpace(steeringNote) ? "Continue where you left off." : steeringNote!)
            : (cfg.Prompt ?? string.Empty);
        string rendered = prompt;
        if (rendering is not null)
            rendered = await rendering.RenderAsync(prompt, ctx.Run.Id, wi, ctx.Run.PreviousNodeOutput);

        if (isSteering && scopeFactory is not null)
            await ClearSteeringNoteAsync(scopeFactory, ctx.Run.Id);

        yield return new NodeOutcome.NodeStarting(rendered);

        NodeExecutionResult result;
        try
        {
            var manageSession = cfg.UseSession ?? false;
            string? incomingSessionId = null;
            if (manageSession && !string.IsNullOrWhiteSpace(cfg.SessionPlaceholder))
            {
                var sessions = sp.GetRequiredService<ILoopRunStore>();
                var sessionBinding = await sessions.GetSessionBindingAsync(ctx.Run.Id, ctx.Node.NodeType.ToString(), cfg.SessionPlaceholder!);
                incomingSessionId = sessionBinding?.SessionId;
            }
            // Steering forces continuation of the live session captured before
            // the halt, regardless of the node's UseSession config.
            if (isSteering)
                incomingSessionId = ctx.Run.CurrentAiSessionId;
            var adapterConfigDict = ParseAdapterConfig(cfg.AdapterConfig);
            var runContext = new LoopRunContext(
                ctx.Run.Id, wi.Id, wi.Title, wi.Description ?? string.Empty,
                ctx.Run.WorktreePath ?? string.Empty, ctx.Run.BranchName ?? string.Empty,
                new List<string>(), ctx.Run.PreviousNodeOutput);
            var runId = ctx.Run.Id;
            var agentCtx = new AgentExecutionContext(
                provider, rendered, runContext, 0, ctx.CancellationToken,
                ctx.ProgressCallback, adapterConfigDict, cfg.ToolAllowlist,
                SessionId: incomingSessionId, IncomingSessionId: incomingSessionId,
                ManageSession: manageSession,
                OnSessionId: scopeFactory is null ? null : sid => PersistSessionId(scopeFactory, runId, sid));
            result = await adapter.ExecuteAsync(agentCtx);
        }
        catch (Exception ex)
        {
            result = NodeExecutionResult.Fail(ex.Message);
        }
        finally
        {
            concurrency?.Exit(providerId);
        }

        if (result.Success)
        {
            if (cfg.UseSession ?? false
                && !string.IsNullOrWhiteSpace(cfg.SessionPlaceholder)
                && !string.IsNullOrWhiteSpace(result.SessionId))
            {
                yield return new NodeOutcome.SessionBound(cfg.SessionPlaceholder!, result.SessionId!);
            }
            if (!string.IsNullOrEmpty(result.Output))
            {
                if (cfg.MatchRules is { Count: > 0 })
                {
                    // First rule whose pattern matches routes to its named custom
                    // edge; no match falls through to the default OnSuccess edge.
                    foreach (var rule in cfg.MatchRules)
                    {
                        if (!string.IsNullOrWhiteSpace(rule.Pattern) && !string.IsNullOrWhiteSpace(rule.EdgeName)
                            && System.Text.RegularExpressions.Regex.IsMatch(result.Output, rule.Pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        {
                            yield return new NodeOutcome.Success(EdgeType.Custom, result.Output, rule.EdgeName);
                            yield break;
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(cfg.RejectPattern)
                    && System.Text.RegularExpressions.Regex.IsMatch(result.Output, cfg.RejectPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    // Legacy reject pattern: route a matching output to the
                    // fallback edge, preserving pre-custom-edge behavior.
                    yield return new NodeOutcome.Success(EdgeType.OnFailure, result.Output);
                    yield break;
                }
            }
            yield return new NodeOutcome.Success(EdgeType.OnSuccess, result.Output);
        }
        else
        {
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, result.Error ?? "AI adapter failed", result.Output);
        }
    }

    /// <summary>
    /// Persist the live AI session id captured mid-stream. Runs synchronously on
    /// the adapter's stream task in a fresh DI scope (fires once per run);
    /// best-effort — capturing the session is observational and must never take
    /// down the stream read.
    /// </summary>
    private static void PersistSessionId(IServiceScopeFactory factory, Guid runId, string sessionId)
    {
        try
        {
            using var scope = factory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
            store.SetCurrentAiSessionIdAsync(runId, sessionId).GetAwaiter().GetResult();
        }
        catch { /* best-effort */ }
    }

    private static async Task ClearSteeringNoteAsync(IServiceScopeFactory factory, Guid runId)
    {
        try
        {
            using var scope = factory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
            await store.ClearSteeringNoteAsync(runId);
        }
        catch { /* best-effort */ }
    }

    private static Dictionary<string, object?>? ParseAdapterConfig(JsonElement? cfg)
    {
        if (cfg is null) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(cfg.Value.GetRawText());
        }
        catch { return null; }
    }
}
