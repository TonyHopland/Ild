using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        // The session fields are template fields like the prompt, but with a
        // narrower grammar ({{Var.<name>}} only) and no tolerance for an unset
        // variable — see SessionPlaceholderTemplate. They are resolved once,
        // here, and the single resolved value is threaded through both the
        // binding lookup and the bind below: resolving at each site could let
        // them diverge, and the run would resume one session while recording
        // another. Resolved before the concurrency slot is claimed so a failure
        // cannot leak it.
        var manageSession = cfg.UseSession ?? false;
        string? sessionPlaceholder = null;
        string? forkFromPlaceholder = null;
        if (manageSession)
        {
            string? sessionError;
            try
            {
                var variables = await LoadSessionVariablesAsync(sp, ctx.Run.Id, cfg);
                var session = SessionPlaceholderTemplate.Resolve("sessionPlaceholder", cfg.SessionPlaceholder, variables);
                var forkFrom = SessionPlaceholderTemplate.Resolve("forkFromPlaceholder", cfg.ForkFromPlaceholder, variables);
                sessionError = session.Error ?? forkFrom.Error;
                sessionPlaceholder = session.Value;
                forkFromPlaceholder = forkFrom.Value;
            }
            catch (Exception ex)
            {
                sessionError = $"Could not read this run's loop variables to resolve the session placeholder: {ex.Message}";
            }
            if (sessionError is not null)
            {
                yield return new NodeOutcome.Fail(EdgeType.OnFailure, sessionError);
                yield break;
            }
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
        //
        // Continuing needs a session to continue. A run halted or throttle-parked
        // before the adapter ever reported one resumes COLD instead: the node's
        // own prompt, rendered as normal, with the human's note appended. Sending
        // "Continue where you left off." into a brand-new session would hand the
        // agent the follow-up to a conversation it never had.
        var steeringNote = ctx.Run.SteeringNote;
        var isResuming = steeringNote is not null;
        var isSteering = isResuming && !string.IsNullOrWhiteSpace(ctx.Run.CurrentAiSessionId);
        var coldRestartNote = isResuming && !isSteering && !string.IsNullOrWhiteSpace(steeringNote)
            ? steeringNote
            : null;

        var prompt = isSteering
            ? (string.IsNullOrWhiteSpace(steeringNote) ? "Continue where you left off." : steeringNote!)
            : (cfg.Prompt ?? string.Empty);

        // The node's configured prompt is a template field: it is rendered here,
        // exactly once, and what substitution pulls in is never re-scanned. A
        // steering note is not a template field — it is the human's own words,
        // typed at halt→resume — so it reaches the agent verbatim, for the same
        // reason a chat turn is not rendered (ADR-0011). That holds for the note
        // appended to a cold restart too: it is appended after rendering.
        string rendered = prompt;
        if (!isSteering && rendering is not null)
            rendered = await rendering.RenderAsync(prompt, ctx.Run.Id, wi, ctx.Run.PreviousNodeOutput);
        if (coldRestartNote is not null)
            rendered = string.IsNullOrWhiteSpace(rendered) ? coldRestartNote : $"{rendered}\n\n{coldRestartNote}";

        // Consumed either way — a note left behind would re-apply on every later
        // visit to this node.
        if (isResuming && scopeFactory is not null)
            await ClearSteeringNoteAsync(scopeFactory, ctx.Run.Id);

        yield return new NodeOutcome.NodeStarting(rendered);

        NodeExecutionResult result;
        try
        {
            string? incomingSessionId = null;
            string? forkFromSessionId = null;
            if (manageSession && !isSteering && !string.IsNullOrWhiteSpace(forkFromPlaceholder))
            {
                // Fork: re-seed from the source session on every execution, so a
                // node in a loop restarts from the (frozen) base each time. The
                // destination's own prior binding is intentionally ignored — a
                // fresh copy is materialized under a new id and continued on.
                var sessions = sp.GetRequiredService<ILoopRunStore>();
                var sourceBinding = await sessions.GetSessionBindingAsync(ctx.Run.Id, ctx.Node.NodeType.ToString(), forkFromPlaceholder!);
                if (!string.IsNullOrWhiteSpace(sourceBinding?.SessionId))
                {
                    forkFromSessionId = sourceBinding!.SessionId;
                    incomingSessionId = Guid.NewGuid().ToString();
                }
                // No bound source session: fall through as a normal new AI node
                // (fresh session, no fork) — no fail-fast, no validation gate.
            }
            else if (manageSession && !string.IsNullOrWhiteSpace(sessionPlaceholder))
            {
                var sessions = sp.GetRequiredService<ILoopRunStore>();
                var sessionBinding = await sessions.GetSessionBindingAsync(ctx.Run.Id, ctx.Node.NodeType.ToString(), sessionPlaceholder!);
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
            // Parenthesised deliberately: `??` binds looser than `&&`, so the
            // unbracketed form collapsed to `cfg.UseSession` alone and could
            // bind a null placeholder or a null session id.
            if (manageSession
                && !string.IsNullOrWhiteSpace(sessionPlaceholder)
                && !string.IsNullOrWhiteSpace(result.SessionId))
            {
                yield return new NodeOutcome.SessionBound(sessionPlaceholder!, result.SessionId!);
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
            // A throttle is not a failing node: the work never got to happen. The
            // adapter classifies when it has a structured signal; otherwise the
            // shared text classifier reads the failure — output first, because
            // every adapter renders a non-zero exit as "exit=N stderr=..." and the
            // provider's notice ("You've hit your session limit · resets 9:40am")
            // arrives as the agent's output. The two texts are matched against
            // different rule sets, since this output is also where an agent's own
            // narration lands — see AiFailureClassifier.
            var failure = result.Failure != FailureKind.Unknown
                ? result.Failure
                : AiFailureClassifier.Classify(result.Output, result.Error);
            if (failure == FailureKind.Interrupted)
            {
                // Which Resume the human is about to get depends on whether the
                // session id reached the DB, because that column is the exact
                // value the resumed node continues from (see the steering branch
                // above). Reload rather than trust the in-memory run: the capture
                // is written from the adapter's stream task in its own scope, so
                // this instance has not seen it. A failed reload leaves the
                // pre-node value, which promises the less of the two Resumes.
                await ReloadRunAsync(sp, ctx.Run);
                yield return new NodeOutcome.Interrupted(
                    InterruptedReason(ctx.Run.CurrentAiSessionId, result.Error), result.Output);
                yield break;
            }
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, result.Error ?? "AI adapter failed", result.Output);
        }
    }

    /// <summary>
    /// What the parked run tells the human, which is really one thing: what
    /// pressing Resume will do. With a captured session it continues the same
    /// agent conversation; without one (throttled before the session started, or
    /// the capture write was lost) it re-runs the node from scratch. A throttle
    /// parks either way — the value the park buys is control over <em>when</em>
    /// the next attempt fires, and that survives losing the session.
    /// </summary>
    private static string InterruptedReason(string? sessionId, string? adapterError)
    {
        var message = string.IsNullOrWhiteSpace(sessionId)
            ? "Provider throttled before the session started — Resume will restart this node from the beginning."
            : "Provider throttled this AI node — Resume will continue the same agent session where it left off.";
        return string.IsNullOrWhiteSpace(adapterError)
            ? message
            : $"{message} (adapter: {adapterError.Trim()})";
    }

    /// <summary>
    /// Refresh the run instance with the row's current column values, so a
    /// column another scope wrote while the node ran (here: the mid-stream
    /// session-id capture) is visible. Best-effort — the caller only reads, and
    /// the stale value it falls back to understates what Resume offers.
    /// </summary>
    private static async Task ReloadRunAsync(IServiceProvider sp, LoopRun run)
    {
        try { await sp.GetRequiredService<ILoopRunStore>().ReloadAsync(run); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// The run's loop variables, keyed case-insensitively like the prompt
    /// pipeline's — or null when neither session field is templated, in which
    /// case a literal session name never touches the store. Unlike
    /// <see cref="PromptRenderingService"/> this load is <em>not</em>
    /// best-effort: a failure here would make every variable look unset, and
    /// the caller turns that into a node failure rather than a wrong session.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>?> LoadSessionVariablesAsync(
        IServiceProvider sp, Guid runId, NodeConfig.Ai cfg)
    {
        if (!SessionPlaceholderTemplate.IsTemplated(cfg.SessionPlaceholder)
            && !SessionPlaceholderTemplate.IsTemplated(cfg.ForkFromPlaceholder))
            return null;

        var variables = await sp.GetRequiredService<ILoopRunStore>().GetVariablesAsync(runId);
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in variables)
            byName[v.Name] = v.Value;   // indexer, not ToDictionary: names differing only in case must not throw
        return byName;
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
    ///
    /// <para>
    /// Still best-effort, but no longer silent. Halt→resume and the throttle park
    /// both continue from this column, so a lost write is the difference between
    /// Resume continuing the agent's conversation and Resume re-running the node
    /// cold. Killing a live AI node over a bookkeeping write would be worse, so
    /// the failure is logged rather than raised — a degraded park has to be
    /// explainable after the fact.
    /// </para>
    /// </summary>
    private static void PersistSessionId(IServiceScopeFactory factory, Guid runId, string sessionId)
    {
        try
        {
            using var scope = factory.CreateScope();
            var sp = scope.ServiceProvider;
            try
            {
                sp.GetRequiredService<ILoopRunStore>()
                    .SetCurrentAiSessionIdAsync(runId, sessionId).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                sp.GetService<ILogger<AINodeExecutor>>()?.LogWarning(ex,
                    "Run {RunId}: failed to persist AI session id; a halt or throttle park will resume this node from the beginning instead of the live session",
                    runId);
            }
        }
        catch { /* the scope itself is gone — nothing left to log through */ }
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
