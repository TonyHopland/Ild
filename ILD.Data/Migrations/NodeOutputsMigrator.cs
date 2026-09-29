using System.Text.Json;
using System.Text.Json.Nodes;
using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Migrations;

/// <summary>
/// Idempotent data migration to declared outputs (ADR-0023). Before it, a node's
/// output names were spread over <c>customEdges</c>, AI <c>matchRules</c>,
/// Condition <c>cases</c>/<c>defaultEdge</c>, a hardcoded PR list and the edge
/// rows themselves. This rewrites the config of every node of every template
/// version — old versions too, since they can still be viewed, cloned and run —
/// so that it declares all of them in <c>outputs</c>, through the same
/// <see cref="LoopOutputs.UpgradeLegacyConfig"/> the document upgrader uses.
///
/// Edge rows are only read: routing follows them alone, so a run in flight or
/// parked across the upgrade keeps taking the same routes. A node is written
/// only when its content changes, so a second run rewrites nothing.
/// </summary>
public static class NodeOutputsMigrator
{
    /// <summary>Runs the migration; returns the number of nodes rewritten.</summary>
    public static async Task<int> MigrateAsync(AppDbContext db, CancellationToken ct = default)
    {
        var nodes = await db.LoopNodes.ToListAsync(ct);
        if (nodes.Count == 0) return 0;

        var outgoing = (await db.LoopNodeEdges
                .Select(e => new { e.SourceNodeId, e.EdgeType, e.Name })
                .ToListAsync(ct))
            .ToLookup(e => e.SourceNodeId, e => (e.EdgeType, e.Name));

        var migrated = 0;
        foreach (var node in nodes)
        {
            if (!TryReadConfig(node.Config, out var config, out var replaced))
                continue;

            if (LoopOutputs.UpgradeLegacyConfig(node.NodeType, config, outgoing[node.Id]) || replaced)
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
    /// The stored config as an object. A null, blank or non-object config reads
    /// as an empty object that must be written back (<paramref name="replaced"/>).
    /// Text that is not JSON at all is left alone rather than overwritten.
    /// </summary>
    private static bool TryReadConfig(string? stored, out JsonObject config, out bool replaced)
    {
        config = new JsonObject();
        replaced = true;
        if (string.IsNullOrWhiteSpace(stored)) return true;

        try
        {
            if (JsonNode.Parse(stored) is JsonObject parsed)
            {
                config = parsed;
                replaced = false;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
