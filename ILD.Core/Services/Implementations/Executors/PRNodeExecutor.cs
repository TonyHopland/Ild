using System.Text.RegularExpressions;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations.Executors;

public sealed class PRNodeExecutor : INodeExecutor
{
    private static readonly Regex Placeholder = new(@"\{\{\s*([A-Za-z][A-Za-z0-9_.:/\\-]*)\s*\}\}", RegexOptions.Compiled);

    public NodeType NodeType => NodeType.PR;
    private readonly IServiceProvider _sp;

    public PRNodeExecutor(IServiceProvider sp)
    {
        _sp = sp;
    }

    public async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx)
    {
        using var scope = _sp.CreateScope();
        var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();

        var wi = await workItemStore.GetByIdAsync(ctx.WorkItem.Id);
        if (wi == null)
            return NodeExecutionResult.Fail("WorkItem not found");

        if (string.IsNullOrEmpty(wi.PrUrl))
        {
            var repo = await workItemStore.GetRepositoryAsync(wi.RepositoryId);
            if (repo == null)
                return NodeExecutionResult.Fail("Repository not found");
            var branch = wi.BranchName ?? $"ild/wi-{wi.Id:N}";
            var target = repo.DefaultBranch ?? "main";

            if (!string.IsNullOrEmpty(wi.WorktreePath) && Directory.Exists(wi.WorktreePath))
            {
                var repoManager = scope.ServiceProvider.GetRequiredService<IRepositoryManager>();

                var diff = await repoManager.GetDiffAsync(wi.WorktreePath);
                if (!string.IsNullOrEmpty(diff))
                {
                    if (!await repoManager.CommitAsync(wi.WorktreePath, wi.Title))
                        return NodeExecutionResult.Fail("Failed to commit uncommitted changes");
                }

                var pushResult = await repoManager.PushAsync(wi.WorktreePath, branch, ctx.CancellationToken);
                if (!pushResult.Success)
                    return NodeExecutionResult.Fail($"Failed to push branch '{branch}' to remote: {pushResult.Error ?? "unknown error"}");

                _ = await repoManager.FetchAsync(wi.WorktreePath, ctx.CancellationToken);
                var ahead = await repoManager.GetCommitsAheadCountAsync(wi.WorktreePath, $"origin/{target}");
                if (ahead == 0)
                    return NodeExecutionResult.Fail($"Branch '{branch}' has no commits ahead of 'origin/{target}'; no changes were made");
            }

            var remote = scope.ServiceProvider.GetRequiredService<IRemoteProvider>();

            var prBody = ResolvePrDescription(ctx.Node.Config, wi, ctx.PreviousNodeOutput);

            var prResult = await remote.CreatePullRequestAsync(
                repo.CloneUrl,
                branch,
                target,
                wi.Title,
                prBody);

            if (prResult == null)
                return NodeExecutionResult.Fail("PR creation returned no result");

            if (!string.IsNullOrEmpty(prResult.Error))
                return NodeExecutionResult.Fail($"PR creation failed: {prResult.Error}");

            wi.PrUrl = prResult.HtmlUrl ?? prResult.Url;
            await workItemStore.UpdateAsync(wi);
        }

        return NodeExecutionResult.Ok(wi.PrUrl);
    }

    static string ResolvePrDescription(string? nodeConfig, WorkItem wi, string? previousNodeOutput)
    {
        var cfg = string.IsNullOrEmpty(nodeConfig)
            ? (Dictionary<string, object>?)null
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(nodeConfig);
        var template = cfg?.GetValueOrDefault("prDescriptionTemplate")?.ToString();

        if (string.IsNullOrEmpty(template))
            return wi.Description ?? "";

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["WorkItem.Title"] = wi.Title,
            ["WorkItem.Description"] = wi.Description ?? "",
            ["PreviousNode.Output"] = previousNodeOutput ?? "",
            ["Node.Input"] = previousNodeOutput ?? "",
        };

        return Placeholder.Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            return values.TryGetValue(key, out var v) ? v ?? "" : m.Value;
        });
    }
}
