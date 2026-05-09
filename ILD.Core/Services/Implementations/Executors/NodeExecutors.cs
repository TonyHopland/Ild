using System.Text.Json;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Implementations;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations.Executors;

public sealed class StartNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.Start;
    private readonly IRepositoryManager _repo;
    private readonly IServiceProvider _sp;

    public StartNodeExecutor(IRepositoryManager repo, IServiceProvider sp)
    {
        _repo = repo;
        _sp = sp;
    }

    public string DescribeInput(NodeExecutionContext ctx)
        => JsonSerializer.Serialize(new { nodeType = NodeType.ToString(), message = "initialized" });

    public async Task<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        using var scope = _sp.CreateScope();
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        var providerStore = scope.ServiceProvider.GetRequiredService<IProviderStore>();

        var wi = ctx.WorkItem;
        if (wi.RepositoryId == null)
            return new NodeOutcome.Failed(
                "WorkItem has no repository attached; refusing to run loop without an isolated worktree.");
        var repo = await providerStore.GetRepositoryByIdAsync(wi.RepositoryId.Value);
        if (repo == null)
            return new NodeOutcome.Failed(
                "WorkItem has no repository attached; refusing to run loop without an isolated worktree.");

        var run = await loopRunStore.GetByIdAsync(ctx.Run.Id);
        if (string.IsNullOrEmpty(run?.WorktreePath) || !Directory.Exists(run.WorktreePath))
        {
            var branch = run.BranchName ?? $"ild/wi-{wi.Id:N}";
            var basePath = repo.WorktreesPath;
            var cloned = false;
            if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(Path.Combine(basePath, ".git")))
            {
                basePath = Path.GetFullPath(Path.Combine("data", "repos", repo.Id.ToString("N")));
                Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
                if (!Directory.Exists(Path.Combine(basePath, ".git")))
                {
                    var result = await _repo.CloneAsync(repo.CloneUrl, basePath, ctx.CancellationToken);
                    if (!result.Success) return new NodeOutcome.Failed($"git clone failed: {result.Error}");
                    cloned = true;
                }
            }
            // Pull existing base repo to ensure it's up to date (best-effort, skip if just cloned)
            if (!cloned)
            {
                try { await _repo.PullAsync(basePath, ctx.CancellationToken); } catch { }
            }
            var path = await _repo.CreateWorktreeAsync(basePath, branch);
            run.WorktreePath = path;
            run.BranchName = branch;
            await loopRunStore.UpdateRunAsync(run);
        }
        if (string.IsNullOrEmpty(run?.WorktreePath) || !Directory.Exists(run.WorktreePath))
            return new NodeOutcome.Failed(
                $"Failed to materialize worktree for WorkItem {wi.Id}; refusing to continue.");
        return new NodeOutcome.Succeeded($"worktree={run.WorktreePath}");
    }
}

public sealed class CmdNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.Cmd;

    private readonly IProcessRunner _runner;

    public CmdNodeExecutor(IProcessRunner runner) { _runner = runner; }

    public string DescribeInput(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Cmd>(ctx.Node.Config);
        return JsonSerializer.Serialize(new { nodeType = NodeType.ToString(), command = cfg.Command ?? "(no command)" });
    }

    public async Task<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Cmd>(ctx.Node.Config);
        if (string.IsNullOrEmpty(cfg.Command))
            return new NodeOutcome.Failed("Cmd node missing 'command' config");
        var command = cfg.Command;

        var cwd = ctx.WorkItem.WorktreePath;
        if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd))
            return new NodeOutcome.Failed(
                "Cmd node requires a valid WorkItem.WorktreePath; refusing to run outside the loop's worktree.");

        var timeout = TimeSpan.FromSeconds(ctx.Node.TimeoutSeconds > 0 ? ctx.Node.TimeoutSeconds : 300);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var r = await _runner.RunAsync("/bin/sh", new[] { "-c", command }, cwd, timeoutCts.Token);
            return r.Success
                ? new NodeOutcome.Succeeded(r.StdOut)
                : new NodeOutcome.Failed($"exit={r.ExitCode} stderr={r.StdErr}", r.StdOut);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ctx.CancellationToken.IsCancellationRequested)
        {
            return new NodeOutcome.Failed($"command timed out after {timeout}");
        }
    }
}

public sealed class AINodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.AI;
    private readonly IServiceProvider _sp;

    public AINodeExecutor(IServiceProvider sp) { _sp = sp; }

    public string DescribeInput(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Ai>(ctx.Node.Config);
        var initial = cfg.InitialPrompt ?? "";
        var loopPrompt = cfg.LoopPrompt ?? initial;
        var payload = new
        {
            nodeType = NodeType.ToString(),
            prompt = initial,
            loopPrompt,
            context = new
            {
                workItemTitle = ctx.WorkItem.Title,
                workItemDescription = ctx.WorkItem.Description,
                previousNodeOutput = ctx.PreviousNodeOutput,
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    public async Task<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Ai>(ctx.Node.Config);
        try
        {
            using var scope = _sp.CreateScope();
            var providerStore = scope.ServiceProvider.GetRequiredService<IProviderStore>();
            var provider = await ResolveProviderAsync(providerStore, cfg.AiProviderId);
            if (provider == null) return new NodeOutcome.Failed("No AI provider found");

            var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
            var run = await loopRunStore.GetByIdAsync(ctx.Run.Id);

            string? sessionId = null;
            string? incomingSessionId = null;
            var sessionInput = cfg.SessionInput ?? "incoming";
            if (sessionInput == "incoming" && run is not null)
            {
                incomingSessionId = ResolveSessionForProvider(run.SessionsJson, provider.Id);
                sessionId = incomingSessionId;
            }

            var registry = scope.ServiceProvider.GetRequiredService<IAgentAdapterRegistry>();
            var adapter = registry.ResolveForProvider(provider)();

            var executionCount = await CountNodeVisitsAsync(scope.ServiceProvider, ctx.Run.Id, ctx.Node.Id);

            var eventLogService = scope.ServiceProvider.GetRequiredService<IEventLogService>();
            var eventEntries = await eventLogService.GetByRunIdAsync(ctx.Run.Id);
            var eventLogSummary = eventEntries
                .Select(e => $"{e.EventType}: {e.Data}")
                .ToList();

            // Surface session resolution to the run's event log so the user
            // can see in the UI whether the AI node is resuming or starting
            // fresh on each visit. Without this it's invisible whether
            // SessionsJson is round-tripping or not.
            try
            {
                var resolvedMsg = string.IsNullOrEmpty(sessionId)
                    ? $"sessionInput={sessionInput} provider={provider.Id} resolved=<none> (sessionsJson={(string.IsNullOrEmpty(run?.SessionsJson) ? "<null>" : run!.SessionsJson)})"
                    : $"sessionInput={sessionInput} provider={provider.Id} resolved={sessionId}";
                await eventLogService.AppendAsync(ctx.Run.Id, AiSessionResolvedEvent, resolvedMsg, ctx.Node.Id, runNodeId: ctx.RunNode.Id);
            }
            catch { /* best-effort observability */ }

            var runContext = new LoopRunContext(
                ctx.Run.Id,
                ctx.WorkItem.Id,
                ctx.WorkItem.Title,
                ctx.WorkItem.Description ?? "",
                ctx.WorkItem.WorktreePath ?? "",
                ctx.WorkItem.BranchName ?? "",
                eventLogSummary,
                ctx.PreviousNodeOutput);

            var timeout = TimeSpan.FromSeconds(ctx.Node.TimeoutSeconds > 0 ? ctx.Node.TimeoutSeconds : 300);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
            timeoutCts.CancelAfter(timeout);

            var initialPrompt = cfg.InitialPrompt ?? "";
            var loopPrompt = cfg.LoopPrompt ?? initialPrompt;

            var agentCtx = new AgentExecutionContext(
                provider,
                initialPrompt,
                loopPrompt,
                runContext,
                executionCount,
                timeoutCts.Token,
                ctx.ProgressCallback,
                ExtractAdapterConfig(cfg.AdapterConfig),
                sessionId,
                incomingSessionId);

            var result = await adapter.ExecuteAsync(agentCtx);

            if (result.Success && result.Output != null && !string.IsNullOrEmpty(cfg.RejectPattern))
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(result.Output, cfg.RejectPattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    return new NodeOutcome.Failed(
                        "AI rejected: output matched rejectPattern", result.Output);
                }
            }

            if (run is not null)
            {
                var sessionOutput = cfg.SessionOutput ?? "current";
                string? effectiveSessionId = sessionOutput switch
                {
                    "current" => result.SessionId,
                    "incoming" => result.IncomingSessionId,
                    _ => null
                };

                // Reload the run to avoid clobbering concurrent writes
                // (status/currentNodeId/etc.) made by the engine while the
                // AI was running. EF's `Update(run)` marks every column as
                // Modified, so updating the stale local copy from the
                // start of execution would silently roll back those
                // mutations. Reloading also ensures we merge with the
                // current SessionsJson value if anything else touched it.
                var freshRun = await loopRunStore.GetByIdAsync(ctx.Run.Id) ?? run;
                await UpdateRunSessionAsync(loopRunStore, freshRun, provider.Id, effectiveSessionId);

                try
                {
                    var persistedMsg = effectiveSessionId is null
                        ? $"sessionOutput={sessionOutput} provider={provider.Id} adapterReturned=<null> (sessionsJson cleared for this provider)"
                        : $"sessionOutput={sessionOutput} provider={provider.Id} persisted={effectiveSessionId} sessionsJson={freshRun.SessionsJson}";
                    await eventLogService.AppendAsync(ctx.Run.Id, AiSessionPersistedEvent, persistedMsg, ctx.Node.Id, runNodeId: ctx.RunNode.Id);
                }
                catch { /* best-effort observability */ }
            }

            return NodeOutcome.FromResult(result);
        }
        catch (OperationCanceledException) { return new NodeOutcome.Failed("AI node timed out"); }
        catch (Exception ex) { return new NodeOutcome.Failed(ex.Message); }
    }

    public const string AiSessionResolvedEvent = "AiSessionResolved";
    public const string AiSessionPersistedEvent = "AiSessionPersisted";

    private static async Task<int> CountNodeVisitsAsync(IServiceProvider sp, Guid runId, Guid nodeId)
    {
        var loopRunStore = sp.GetRequiredService<ILoopRunStore>();
        var runNodes = await loopRunStore.GetRunNodesAsync(runId);
        return runNodes.Count(n => n.LoopNodeId == nodeId);
    }

    private static async Task<AiProvider?> ResolveProviderAsync(IProviderStore store, string? providerKey)
    {
        if (string.IsNullOrEmpty(providerKey))
            return await store.GetDefaultAiProviderAsync();

        if (Guid.TryParse(providerKey, out var id))
            return await store.GetAiProviderByIdAsync(id);

        return await store.GetAiProviderByNameAsync(providerKey);
    }

    private static Dictionary<string, object?>? ExtractAdapterConfig(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } el) return null;
        var result = new Dictionary<string, object?>();
        foreach (var prop in el.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Number => prop.Value.GetDouble(),
                JsonValueKind.True or JsonValueKind.False => prop.Value.GetBoolean(),
                JsonValueKind.String => prop.Value.GetString(),
                _ => prop.Value.GetRawText()
            };
        }
        return result;
    }

    private static string? ResolveSessionForProvider(string? sessionsJson, Guid providerId)
    {
        if (string.IsNullOrEmpty(sessionsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(sessionsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                // Tolerate both camelCase (current) and PascalCase (legacy
                // pre-fix payloads in the DB) so existing runs continue to
                // resolve their session after upgrade.
                var pid = TryGetStringInsensitive(entry, "providerId");
                if (pid == null || !Guid.TryParse(pid, out var parsedPid) || parsedPid != providerId)
                    continue;
                return TryGetStringInsensitive(entry, "sessionId");
            }
        }
        catch { }
        return null;
    }

    private static string? TryGetStringInsensitive(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
        }
        return null;
    }

    // SessionsJson is read via raw JsonDocument in ResolveSessionForProvider
    // using camelCase property names (providerId/sessionId), so we must
    // serialize the same shape here. Default System.Text.Json serialization
    // is PascalCase and case-sensitive on read — without these options the
    // reader would silently fail to find any sessions written by the writer
    // and AI nodes would always start a fresh session.
    private static readonly JsonSerializerOptions SessionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static async Task UpdateRunSessionAsync(ILoopRunStore store, LoopRun run, Guid providerId, string? sessionId)
    {
        var sessions = ParseSessions(run.SessionsJson);
        if (sessionId is null)
        {
            sessions.RemoveAll(s => s.ProviderId == providerId.ToString());
        }
        else
        {
            var existing = sessions.Find(s => s.ProviderId == providerId.ToString());
            if (existing is not null)
                existing.SessionId = sessionId;
            else
                sessions.Add(new RunSessionEntry(providerId.ToString(), sessionId));
        }
        run.SessionsJson = JsonSerializer.Serialize(sessions, SessionJsonOptions);
        await store.UpdateRunAsync(run);
    }

    private static List<RunSessionEntry> ParseSessions(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new List<RunSessionEntry>();
        try { return JsonSerializer.Deserialize<List<RunSessionEntry>>(json, SessionJsonOptions) ?? new List<RunSessionEntry>(); }
        catch { return new List<RunSessionEntry>(); }
    }

    private sealed class RunSessionEntry
    {
        public string ProviderId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public RunSessionEntry(string providerId, string sessionId)
        {
            ProviderId = providerId;
            SessionId = sessionId;
        }
    }
}

public sealed class HumanNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.Human;
    private readonly IServiceProvider _sp;

    public HumanNodeExecutor(IServiceProvider sp) { _sp = sp; }

    public string DescribeInput(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Human>(ctx.Node.Config);
        return JsonSerializer.Serialize(new { nodeType = NodeType.ToString(), prompt = cfg.Prompt ?? "(no prompt)" });
    }

    public async Task<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Human>(ctx.Node.Config);
        var rendered = await RenderPromptAsync(_sp, cfg.Prompt, ctx);
        if (!string.IsNullOrEmpty(rendered))
        {
            // Surface the resolved prompt via the event log so the UI can
            // display exactly what the human is being asked. We deliberately
            // do NOT stash it on the run-node Output: that slot belongs to
            // the human's eventual answer.
            using var scope = _sp.CreateScope();
            var eventLog = scope.ServiceProvider.GetService<IEventLogService>();
            if (eventLog != null)
            {
                try
                {
                    await eventLog.AppendAsync(
                        ctx.Run.Id,
                        HumanPromptRenderedEvent,
                        rendered,
                        ctx.Node.Id,
                        runNodeId: ctx.RunNode.Id);
                }
                catch { /* best-effort observability */ }
            }
        }
        return new NodeOutcome.Suspended(
            "Human node awaiting input",
            SuspendKind.HumanInput);
    }

    public const string HumanPromptRenderedEvent = "HumanPromptRendered";

    private static async Task<string?> RenderPromptAsync(IServiceProvider sp, string? template, NodeExecutionContext ctx)
    {
        if (string.IsNullOrEmpty(template)) return null;
        using var scope = sp.CreateScope();
        var resolver = scope.ServiceProvider.GetService<IPromptTemplateResolver>() ?? new PromptTemplateResolver();
        var eventLogService = scope.ServiceProvider.GetService<IEventLogService>();
        IReadOnlyList<string>? summary = null;
        if (eventLogService != null)
        {
            try
            {
                var entries = await eventLogService.GetByRunIdAsync(ctx.Run.Id);
                summary = entries.Select(e => $"{e.EventType}: {e.Data}").ToList();
            }
            catch { /* event log is best-effort */ }
        }
        return resolver.Render(template, new PromptContext(
            WorkItemTitle: ctx.WorkItem.Title,
            WorkItemDescription: ctx.WorkItem.Description,
            PreviousNodeOutput: ctx.PreviousNodeOutput,
            EventLogSummary: summary,
            WorktreePath: ctx.WorkItem.WorktreePath));
    }
}

public sealed class CleanupNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.Cleanup;
    private readonly IRepositoryManager _repo;
    private readonly IServiceProvider _sp;

    public CleanupNodeExecutor(IRepositoryManager repo, IServiceProvider sp)
    {
        _repo = repo;
        _sp = sp;
    }

    public string DescribeInput(NodeExecutionContext ctx)
        => JsonSerializer.Serialize(new { nodeType = NodeType.ToString(), message = "cleanup" });

    public async Task<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        using var scope = _sp.CreateScope();
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();

        var run = await loopRunStore.GetByIdAsync(ctx.Run.Id);
        if (run == null) return new NodeOutcome.Failed("LoopRun not found");

        string? worktreePath = run.WorktreePath;
        if (!string.IsNullOrEmpty(worktreePath) && Directory.Exists(worktreePath))
        {
            try
            {
                await _repo.DestroyWorktreeAsync(worktreePath);
            }
            catch (Exception ex)
            {
                run.WorktreePath = null;
                await loopRunStore.UpdateRunAsync(run);
                return new NodeOutcome.Terminal($"cleanup failed: {ex.Message} (path: {worktreePath})");
            }
            run.WorktreePath = null;
            await loopRunStore.UpdateRunAsync(run);
            return new NodeOutcome.Terminal($"worktree destroyed: {worktreePath}");
        }

        run.WorktreePath = null;
        await loopRunStore.UpdateRunAsync(run);
        return new NodeOutcome.Terminal("cleanup skipped: no worktree");
    }
}
