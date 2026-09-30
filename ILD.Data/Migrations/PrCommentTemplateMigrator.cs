using System.Text.Json;
using System.Text.Json.Nodes;
using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Migrations;

/// <summary>
/// Idempotent data migration that drops <c>prCommentTemplate</c> from every
/// stored node config, in every template version. The PR node no longer reads
/// it, so it only costs space in the config an agent is handed.
/// </summary>
public static class PrCommentTemplateMigrator
{
    private const string Key = "prCommentTemplate";

    /// <summary>Runs the migration; returns the number of nodes rewritten.</summary>
    public static async Task<int> MigrateAsync(AppDbContext db, CancellationToken ct = default)
    {
        // Configs are stored compactly, so the key always reads "prCommentTemplate":
        // and every run after the first is a no-op query.
        var nodes = await db.LoopNodes
            .Where(n => n.Config != null && n.Config.Contains("\"" + Key + "\":"))
            .ToListAsync(ct);

        var migrated = 0;
        foreach (var node in nodes)
        {
            JsonObject? config;
            try
            {
                config = JsonNode.Parse(node.Config!) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (config is null || !config.Remove(Key)) continue;
            node.Config = config.ToJsonString();
            migrated++;
        }

        if (migrated > 0)
            await db.SaveChangesAsync(ct);
        return migrated;
    }
}
