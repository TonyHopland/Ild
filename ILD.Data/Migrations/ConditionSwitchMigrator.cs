using System.Text.Json.Nodes;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Migrations;

/// <summary>
/// One-time, idempotent data migration that converts legacy true/false
/// Condition nodes to the switch model (an ordered list of <c>cases</c> each
/// routing to a named custom edge, plus a <c>defaultEdge</c>).
///
/// A pre-switch Condition stored its single predicate as top-level
/// <c>variant</c>/<c>subject</c>/<c>pattern</c>/<c>tag</c> keys and routed
/// through fixed <c>true</c>/<c>false</c> custom edges. The executor still reads
/// that shape at runtime (see <c>ConditionNodeExecutor.NormalizeCases</c>), so
/// old loops keep working untouched; this migration rewrites the persisted
/// config so the editor and validator see the new shape directly. The existing
/// <c>true</c>/<c>false</c> edges are preserved as-is — the synthesized case
/// routes to <c>true</c> and the default edge is <c>false</c>, matching the old
/// routing exactly, so no edge rows change.
///
/// For every Condition node (across all persisted template versions) that still
/// carries a top-level <c>variant</c> and no <c>cases</c>, this:
/// <list type="number">
///   <item>moves <c>variant</c>/<c>subject</c>/<c>pattern</c>/<c>tag</c> into a
///         single <c>cases</c> entry whose <c>edgeName</c> is "true";</item>
///   <item>sets <c>defaultEdge</c> to "false";</item>
///   <item>removes the now-migrated top-level predicate keys.</item>
/// </list>
/// Running it repeatedly is a cheap no-op once the data is migrated.
/// </summary>
public static class ConditionSwitchMigrator
{
    private const string MatchEdgeName = "true";
    private const string DefaultEdgeName = "false";

    /// <summary>Runs the migration; returns the number of Condition nodes rewritten.</summary>
    public static async Task<int> MigrateAsync(AppDbContext db, CancellationToken ct = default)
    {
        // Only legacy Condition nodes carried a top-level "variant" without a
        // "cases" key. The Contains filters keep the query cheap and make
        // re-runs a no-op once the config has been rewritten.
        var conditionNodes = await db.LoopNodes
            .Where(n => n.NodeType == NodeType.Condition && n.Config != null
                && n.Config.Contains("variant") && !n.Config.Contains("cases"))
            .ToListAsync(ct);
        if (conditionNodes.Count == 0) return 0;

        var migrated = 0;
        foreach (var node in conditionNodes)
        {
            if (!TryRewriteConfig(node.Config!, out var rewritten))
                continue;
            node.Config = rewritten;
            migrated++;
        }

        if (migrated > 0)
            await db.SaveChangesAsync(ct);
        return migrated;
    }

    /// <summary>
    /// Rewrites a legacy Condition config into the switch shape, or returns
    /// false when the config is already migrated (has a non-empty <c>cases</c>)
    /// or carries no legacy <c>variant</c> to convert.
    /// </summary>
    private static bool TryRewriteConfig(string config, out string rewritten)
    {
        rewritten = config;
        if (JsonNode.Parse(config) is not JsonObject obj)
            return false;
        if (obj.TryGetPropertyValue("cases", out var existing) && existing is JsonArray { Count: > 0 })
            return false;
        if (!obj.TryGetPropertyValue("variant", out var variant) || variant is not JsonValue vv
            || !vv.TryGetValue<string>(out var variantValue) || string.IsNullOrWhiteSpace(variantValue))
            return false;

        var caseObj = new JsonObject { ["variant"] = variantValue };
        // Carry only the keys the legacy predicate actually used, so the case
        // mirrors what the executor read before this change.
        if (TryReadString(obj, "subject", out var subject)) caseObj["subject"] = subject;
        if (TryReadString(obj, "pattern", out var pattern)) caseObj["pattern"] = pattern;
        if (TryReadString(obj, "tag", out var tag)) caseObj["tag"] = tag;
        caseObj["edgeName"] = MatchEdgeName;

        obj["cases"] = new JsonArray(caseObj);
        obj["defaultEdge"] = DefaultEdgeName;
        obj.Remove("variant");
        obj.Remove("subject");
        obj.Remove("pattern");
        obj.Remove("tag");

        rewritten = obj.ToJsonString();
        return true;
    }

    private static bool TryReadString(JsonObject obj, string key, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonValue jv
            || !jv.TryGetValue<string>(out var raw))
            return false;
        value = raw;
        return true;
    }
}
