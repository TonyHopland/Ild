using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.RegularExpressions;

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

        // Resolve the provider the loop node itself selects: an explicit GUID
        // pins a specific provider, otherwise the node falls back to the
        // configured default.
        var nodePinsProvider = Guid.TryParse(cfg.AiProviderId, out var parsedId);
        AiProvider? provider;
        if (nodePinsProvider)
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

        // A work item can override the node's provider. The rule is shared with
        // RemoteWorkItemCoordinator's resume gate, which must peek capacity on
        // the same provider this executor claims a slot against.
        var shouldOverride = AiProviderOverrideRule.Applies(
            wi.AiProviderOverride, wi.AiProviderOverrideId, nodePinsProvider);
        if (shouldOverride)
        {
            var overrideProvider = await providerStore.GetAiProviderByIdAsync(wi.AiProviderOverrideId!.Value);
            if (overrideProvider is null)
            {
                yield return new NodeOutcome.Fail(EdgeType.OnFailure, $"Work item AI provider override {wi.AiProviderOverrideId} not found");
                yield break;
            }
            provider = overrideProvider;
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
            string? forkFromSessionId = null;
            if (manageSession && !isSteering && !string.IsNullOrWhiteSpace(cfg.ForkFromPlaceholder))
            {
                // Fork: re-seed from the source session on every execution, so a
                // node in a loop restarts from the (frozen) base each time. The
                // destination's own prior binding is intentionally ignored — a
                // fresh copy is materialized under a new id and continued on.
                var sessions = sp.GetRequiredService<ILoopRunStore>();
                var sourceBinding = await sessions.GetSessionBindingAsync(ctx.Run.Id, ctx.Node.NodeType.ToString(), cfg.ForkFromPlaceholder!);
                if (!string.IsNullOrWhiteSpace(sourceBinding?.SessionId))
                {
                    forkFromSessionId = sourceBinding!.SessionId;
                    incomingSessionId = Guid.NewGuid().ToString();
                }
                // No bound source session: fall through as a normal new AI node
                // (fresh session, no fork) — no fail-fast, no validation gate.
            }
            else if (manageSession && !string.IsNullOrWhiteSpace(cfg.SessionPlaceholder))
            {
                var sessions = sp.GetRequiredService<ILoopRunStore>();
                var sessionBinding = await sessions.GetSessionBindingAsync(ctx.Run.Id, ctx.Node.NodeType.ToString(), cfg.SessionPlaceholder!);
                incomingSessionId = sessionBinding?.SessionId;
            }
            // Steering forces continuation of the live session captured before
            // the halt, regardless of the node's UseSession/fork config.
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
                OnSessionId: scopeFactory is null ? null : sid => PersistSessionId(scopeFactory, runId, sid),
                ForkFromSessionId: forkFromSessionId);
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
            // Stamp the provider onto the usage so the node row records which
            // provider ran it — even when the CLI reported no tokens. Adapters
            // leave AiProvider null; the executor owns the provider identity.
            var usage = (result.Usage ?? new ILD.Data.DTOs.TokenUsage(0, 0, null)) with { AiProvider = provider.Name };
            if (cfg.UseSession ?? false
                && !string.IsNullOrWhiteSpace(cfg.SessionPlaceholder)
                && !string.IsNullOrWhiteSpace(result.SessionId))
            {
                yield return new NodeOutcome.SessionBound(cfg.SessionPlaceholder!, result.SessionId!);
            }
            if (!string.IsNullOrEmpty(result.Output) && cfg.MatchRules is { Count: > 0 })
            {
                // Last match wins: agents narrate ("no reason to reject, so:
                // approve"), so the pattern appearing latest in the output is
                // the verdict. Each rule contributes its own last occurrence,
                // and the rule matching furthest into the text routes. No match
                // falls through to the default OnSuccess edge.
                var winner = SelectLastMatchingRule(cfg.MatchRules, result.Output);
                if (winner is not null)
                {
                    yield return new NodeOutcome.Success(EdgeType.Custom, result.Output, winner.EdgeName, usage);
                    yield break;
                }
            }
            yield return new NodeOutcome.Success(EdgeType.OnSuccess, result.Output, Usage: usage);
        }
        else
        {
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, result.Error ?? "AI adapter failed", result.Output);
        }
    }

    /// <summary>
    /// How long a single rule's pattern may spend scanning the output before it
    /// is abandoned. Last-match-wins evaluates every rule on every run, so one
    /// catastrophically-backtracking pattern would otherwise stall the node.
    /// </summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Picks the match rule whose last occurrence sits furthest into
    /// <paramref name="output"/>, or null when no rule matches. Ties (two rules
    /// matching at the same index) break on the later-ending match first, then
    /// on configured order, so selection is deterministic.
    ///
    /// A rule whose pattern is malformed or too slow is skipped rather than
    /// failing the node: it simply never matches, which is exactly what it did
    /// before last-match-wins forced every rule to be evaluated.
    /// <see cref="LoopTemplateValidator"/> rejects such patterns at save time,
    /// so this only shields loops saved before that check existed.
    /// </summary>
    private static NodeConfig.AiMatchRule? SelectLastMatchingRule(
        IReadOnlyList<NodeConfig.AiMatchRule> rules, string output)
    {
        NodeConfig.AiMatchRule? winner = null;
        var bestIndex = -1;
        var bestEnd = -1;
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern) || string.IsNullOrWhiteSpace(rule.EdgeName))
                continue;

            Match? m;
            try
            {
                // Take this rule's LAST occurrence. Enumerating left-to-right and
                // keeping the final hit — rather than RegexOptions.RightToLeft —
                // keeps ordinary match semantics: RightToLeft also reverses how
                // the pattern itself is applied, which breaks backreferences and
                // changes which alternation branch wins (and so the Length that
                // feeds the tie-break below).
                m = Regex.Matches(output, rule.Pattern, RegexOptions.IgnoreCase, MatchTimeout).LastOrDefault();
            }
            catch (ArgumentException) { continue; }      // malformed pattern
            catch (RegexMatchTimeoutException) { continue; }

            if (m is null || !m.Success) continue;
            var end = m.Index + m.Length;
            if (m.Index > bestIndex || (m.Index == bestIndex && end > bestEnd))
            {
                winner = rule;
                bestIndex = m.Index;
                bestEnd = end;
            }
        }
        return winner;
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
