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
        try { adapter = registry.ResolveForProvider(provider)(); } catch { }
        if (adapter is null)
        {
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, $"No adapter for provider type '{provider.Type}'");
            yield break;
        }

        var providerId = provider.Id;
        if (concurrency is not null && !concurrency.TryEnter(providerId, provider.Parallelism))
        {
            yield return new NodeOutcome.WaitingIld($"AI provider '{provider.Name}' at capacity");
            yield break;
        }

        var prompt = cfg.Prompt ?? string.Empty;
        string rendered = prompt;
        if (rendering is not null)
            rendered = await rendering.RenderAsync(prompt, ctx.Run.Id, wi, ctx.Run.PreviousNodeOutput);

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
            var adapterConfigDict = ParseAdapterConfig(cfg.AdapterConfig);
            var runContext = new LoopRunContext(
                ctx.Run.Id, wi.Id, wi.Title, wi.Description ?? string.Empty,
                ctx.Run.WorktreePath ?? string.Empty, ctx.Run.BranchName ?? string.Empty,
                new List<string>(), ctx.Run.PreviousNodeOutput);
            var agentCtx = new AgentExecutionContext(
                provider, rendered, runContext, 0, ctx.CancellationToken,
                ctx.ProgressCallback, adapterConfigDict, cfg.ToolAllowlist,
                SessionId: incomingSessionId, IncomingSessionId: incomingSessionId,
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
            if (cfg.UseSession ?? false
                && !string.IsNullOrWhiteSpace(cfg.SessionPlaceholder)
                && !string.IsNullOrWhiteSpace(result.SessionId))
            {
                yield return new NodeOutcome.SessionBound(cfg.SessionPlaceholder!, result.SessionId!);
            }
            if (!string.IsNullOrWhiteSpace(cfg.RejectPattern) && !string.IsNullOrEmpty(result.Output)
                && System.Text.RegularExpressions.Regex.IsMatch(result.Output, cfg.RejectPattern))
            {
                yield return new NodeOutcome.Success(EdgeType.OnFailure, result.Output);
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
