using ILD.Core.Enums;
using ILD.Core.Models;
using ILD.Core.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Core.Services.Implementations.Executors;

public sealed class PRNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.PR;
    private readonly IRemoteProvider _remote;
    private readonly Func<AppDbContext> _dbFactory;

    public PRNodeExecutor(IRemoteProvider remote, Func<AppDbContext> dbFactory)
    {
        _remote = remote;
        _dbFactory = dbFactory;
    }

    public async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx)
    {
        await using var db = _dbFactory();

        var wi = await db.WorkItems
            .Include(w => w.Repository)
            .ThenInclude(r => r.RemoteProvider)
            .FirstAsync(w => w.Id == ctx.WorkItem.Id);

        if (wi == null)
            return NodeExecutionResult.Fail("WorkItem not found");

        if (string.IsNullOrEmpty(wi.PrUrl))
        {
            var repo = wi.Repository;
            var branch = wi.BranchName ?? $"ild/wi-{wi.Id:N}";
            var target = repo.DefaultBranch ?? "main";

            var prResult = await _remote.CreatePullRequestAsync(
                repo.CloneUrl,
                branch,
                target,
                wi.Title,
                wi.Description ?? "");

            if (!string.IsNullOrEmpty(prResult.Error))
                return NodeExecutionResult.Fail($"PR creation failed: {prResult.Error}");

            wi.PrUrl = prResult.HtmlUrl ?? prResult.Url;
            await db.SaveChangesAsync();
        }

        return NodeExecutionResult.Ok(wi.PrUrl);
    }
}
