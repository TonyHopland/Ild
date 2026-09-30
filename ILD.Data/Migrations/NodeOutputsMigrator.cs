using System.Text.Json;
using System.Text.Json.Nodes;
using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Migrations;

/// <summary>
/// Idempotent data migration to declared outputs (ADR-0023). Before it, a node's
/// output names were spread over <c>customEdges</c>, AI <c>matchRules</c>,
/// Condition <c>cases</c>/<c>defaultEdge</c>, a hardcoded PR list and the edge
/// rows themselves. This rewrites the config of every node, in every template
/// version — old versions too, since they can still be viewed, cloned and run —
/// that has no <c>outputs</c> yet, so that it declares all of them, through the
/// same <see cref="LoopOutputs.UpgradeLegacyConfig"/> the document upgrader uses.
///
/// A node that already has <c>outputs</c> is never touched, so a second run finds
/// nothing to do. Edge rows are only read: routing follows them alone, so a run in
/// flight or parked across the upgrade keeps taking the same routes.
/// </summary>
public static class NodeOutputsMigrator
{
    /// <summary>Runs the migration; returns the number of nodes rewritten.</summary>
    public static async Task<int> MigrateAsync(AppDbContext db, CancellationToken ct = default)
    {
        // Configs are stored compactly, so an outputs key always reads "outputs":
        // (a string value cannot be followed by a colon, and one inside a string
        // is escaped). The filter makes every run after the first a no-op query.
        var nodes = await db.LoopNodes
            .Where(n => n.Config == null || !n.Config.Contains("\"outputs\":"))
            .ToListAsync(ct);
        if (nodes.Count == 0) return 0;

        var nodeIds = nodes.Select(n => n.Id).ToList();
        var outgoing = (await db.LoopNodeEdges
                .Where(e => nodeIds.Contains(e.SourceNodeId))
                .Select(e => new { e.SourceNodeId, e.EdgeType, e.Name })
                .ToListAsync(ct))
            .ToLookup(e => e.SourceNodeId, e => (e.EdgeType, e.Name));

        var migrated = 0;
        foreach (var node in nodes)
        {
            if (!TryReadConfig(node.Config, out var config))
                continue;

            if (LoopOutputs.UpgradeLegacyConfig(node.NodeType, config, outgoing[node.Id]))
            {
                node.Config = config.ToJsonString();
                migrated++;
            }
        }

        if (migrated > 0)
            await db.SaveChangesAsync(ct);
        return migrated;
    }

    /// <summary>
    /// The stored config as an object; a null, blank or non-object config reads as
    /// an empty one. Text that is not JSON at all is left alone rather than
    /// overwritten.
    /// </summary>
    private static bool TryReadConfig(string? stored, out JsonObject config)
    {
        config = new JsonObject();
        if (string.IsNullOrWhiteSpace(stored)) return true;

        try
        {
            if (JsonNode.Parse(stored) is JsonObject parsed)
                config = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
