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
        var loopRunStore = sp.GetRequiredService<ILoopRunStore>();
        var providerStore = sp.GetRequiredService<IProviderStore>();
        var workItems = sp.GetRequiredService<IWorkItemManager>();
        var registry = sp.GetService<IAgentAdapterRegistry>();
        var concurrency = sp.GetService<IAiProviderConcurrencyTracker>();
        var rendering = sp.GetService<IPromptRenderingService>();
        var sessions = loopRunStore;

        var wi = await workItems.GetWorkItemAsync(ctx.Run.WorkItemId);
        if (wi is null)
        {
            yield return new NodeOutcome.NodeStarting(null);
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "WorkItem not found");
            yield break;
        }

        if (!Guid.TryParse(cfg.AiProviderId, out var providerId))
        {
            yield return new NodeOutcome.NodeStarting(null);
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "AI node missing aiProviderId");
            yield break;
        }
        var provider = await providerStore.GetAiProviderByIdAsync(providerId);
        if (provider is null)
        {
            yield return new NodeOutcome.NodeStarting(null);
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, $"AiProvider {providerId} not found");
            yield break;
        }

        // Provider capacity gate — must come before NodeStarting so the engine
        // does not create a LoopRunNode row that would obscure the queue.
        if (concurrency is not null && !concurrency.HasCapacity(providerId, provider.Parallelism))
        {
            yield return new NodeOutcome.WaitingIld($"AI provider '{provider.Name}' at capacity");
            yield break;
        }

        var run = await loopRunStore.GetByIdAsync(ctx.Run.Id) ?? ctx.Run;
        var prompt = cfg.Prompt ?? string.Empty;
        string rendered = prompt;
        if (rendering is not null)
            rendered = await rendering.RenderAsync(prompt, run.Id, wi, run.PreviousNodeOutput);

        yield return new NodeOutcome.NodeStarting(rendered);

        if (registry is null)
        {
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "No agent adapter registry");
            yield break;
        }
        IAgentAdapter? adapter = null;
        try { adapter = registry.ResolveForProvider(provider)(); } catch { }
        if (adapter is null)
        {
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, $"No adapter for provider type '{provider.Type}'");
            yield break;
        }

        if (concurrency is not null && !concurrency.TryEnter(providerId, provider.Parallelism))
        {
            yield return new NodeOutcome.WaitingIld($"AI provider '{provider.Name}' at capacity");
            yield break;
        }

        NodeExecutionResult result;
        try
        {
            var manageSession = cfg.UseSession ?? false;
            string? incomingSessionId = null;
            if (manageSession && sessions is not null && !string.IsNullOrWhiteSpace(cfg.SessionPlaceholder))
            {
                incomingSessionId = await sessions.GetSessionBindingAsync(run.Id, ctx.Node.NodeType.ToString(), cfg.SessionPlaceholder!).ContinueWith(t => t.Result?.SessionId);
            }
            var adapterConfigDict = ParseAdapterConfig(cfg.AdapterConfig);
            var runContext = new LoopRunContext(
                run.Id, wi.Id, wi.Title, wi.Description ?? string.Empty,
                run.WorktreePath ?? string.Empty, run.BranchName ?? string.Empty,
                new List<string>(), run.PreviousNodeOutput);
            var allRunNodes = await loopRunStore.GetRunNodesAsync(run.Id);
            var executionCount = allRunNodes.Count(rn => rn.LoopNodeId == ctx.Node.Id);
            var agentCtx = new AgentExecutionContext(
                provider, rendered, runContext, executionCount, ctx.CancellationToken,
                ctx.ProgressCallback, adapterConfigDict, cfg.ToolAllowlist,
                SessionId: null, IncomingSessionId: incomingSessionId,
                ManageSession: manageSession);
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
            if (sessions is not null && !string.IsNullOrWhiteSpace(cfg.SessionPlaceholder)
                && !string.IsNullOrWhiteSpace(result.SessionId))
            {
                try { await sessions.UpsertSessionBindingAsync(run.Id, ctx.Node.NodeType.ToString(), cfg.SessionPlaceholder!, result.SessionId!); }
                catch { }
            }
            if (!string.IsNullOrWhiteSpace(cfg.RejectPattern) && !string.IsNullOrEmpty(result.Output)
                && System.Text.RegularExpressions.Regex.IsMatch(result.Output, cfg.RejectPattern))
            {
                yield return new NodeOutcome.Fail(EdgeType.OnReject, "Reject pattern matched", result.Output);
                yield break;
            }
            yield return new NodeOutcome.Success(EdgeType.OnSuccess, result.Output);
        }
        else
        {
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, result.Error ?? "AI adapter failed", result.Output);
        }
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
