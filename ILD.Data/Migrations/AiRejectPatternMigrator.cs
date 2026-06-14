using System.Text.Json.Nodes;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Migrations;

/// <summary>
/// One-time, idempotent data migration that retires the obsolete AI-node
/// <c>rejectPattern</c> config in favour of the named-custom-edge model.
///
/// The schema migration (<c>AddCustomEdgeNameAndExternalActionEdgeName</c>) only
/// adds columns and migrates <c>OnRespond</c> edges; it cannot rewrite the JSON
/// stored in <see cref="LoopNode.Config"/>. Because the template seeder is
/// insert-only, AI nodes seeded before this change still carry
/// <c>rejectPattern</c> with no custom edge — and the executor no longer reads
/// it. Without this fixup a reject-matching review output would fall through to
/// <see cref="EdgeType.OnSuccess"/> (creating a PR) instead of routing to the
/// old failure target.
///
/// For every AI node (across all persisted template versions) that still carries
/// a non-empty <c>rejectPattern</c> and has not already been migrated, this:
/// <list type="number">
///   <item>creates a <see cref="EdgeType.Custom"/> edge named "Reject" to the
///         node the existing <see cref="EdgeType.OnFailure"/> edge targets, so
///         the original reject routing is preserved;</item>
///   <item>appends a <c>matchRules</c> entry
///         <c>{ pattern: &lt;rejectPattern&gt;, edgeName: "Reject" }</c>;</item>
///   <item>removes the <c>rejectPattern</c> key.</item>
/// </list>
/// Running it repeatedly is a cheap no-op once the data is migrated.
/// </summary>
public static class AiRejectPatternMigrator
{
    private const string RejectEdgeName = "Reject";

    /// <summary>Runs the migration; returns the number of AI nodes rewritten.</summary>
    public static async Task<int> MigrateAsync(AppDbContext db, CancellationToken ct = default)
    {
        // Only AI nodes ever carried a rejectPattern. The Contains filter keeps
        // the query cheap and makes re-runs a no-op once the key is gone.
        var aiNodes = await db.LoopNodes
            .Where(n => n.NodeType == NodeType.AI && n.Config != null && n.Config.Contains("rejectPattern"))
            .ToListAsync(ct);
        if (aiNodes.Count == 0) return 0;

        var nodeIds = aiNodes.Select(n => n.Id).ToList();
        var edgesBySource = (await db.LoopNodeEdges
                .Where(e => nodeIds.Contains(e.SourceNodeId))
                .ToListAsync(ct))
            .GroupBy(e => e.SourceNodeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var migrated = 0;
        foreach (var node in aiNodes)
        {
            if (!TryReadRejectPattern(node.Config!, out var pattern))
                continue;

            var outgoing = edgesBySource.GetValueOrDefault(node.Id) ?? new List<LoopNodeEdge>();

            // Preserve the old routing: a matching output used to take the
            // OnFailure edge, so wire a Custom "Reject" edge to the same target.
            var failureTarget = outgoing.FirstOrDefault(e => e.EdgeType == EdgeType.OnFailure);
            var hasRejectEdge = outgoing.Any(e => e.EdgeType == EdgeType.Custom && e.Name == RejectEdgeName);
            if (failureTarget is not null && !hasRejectEdge)
            {
                db.LoopNodeEdges.Add(new LoopNodeEdge
                {
                    Id = Guid.NewGuid(),
                    SourceNodeId = node.Id,
                    TargetNodeId = failureTarget.TargetNodeId,
                    EdgeType = EdgeType.Custom,
                    Name = RejectEdgeName,
                    CreatedAt = DateTime.UtcNow,
                });
            }

            node.Config = RewriteConfig(node.Config!, pattern);
            migrated++;
        }

        if (migrated > 0)
            await db.SaveChangesAsync(ct);
        return migrated;
    }

    /// <summary>
    /// Reads a non-empty string <c>rejectPattern</c> from the config, but only
    /// when the node has not already adopted <c>matchRules</c> (already migrated).
    /// </summary>
    private static bool TryReadRejectPattern(string config, out string pattern)
    {
        pattern = string.Empty;
        if (JsonNode.Parse(config) is not JsonObject obj)
            return false;
        if (obj.TryGetPropertyValue("matchRules", out var rules) && rules is JsonArray { Count: > 0 })
            return false;
        if (!obj.TryGetPropertyValue("rejectPattern", out var value) || value is not JsonValue jv
            || !jv.TryGetValue<string>(out var raw) || string.IsNullOrWhiteSpace(raw))
            return false;
        pattern = raw;
        return true;
    }

    private static string RewriteConfig(string config, string pattern)
    {
        var obj = (JsonObject)JsonNode.Parse(config)!;
        obj.Remove("rejectPattern");
        obj["matchRules"] = new JsonArray(
            new JsonObject
            {
                ["pattern"] = pattern,
                ["edgeName"] = RejectEdgeName,
            });
        return obj.ToJsonString();
    }
}
