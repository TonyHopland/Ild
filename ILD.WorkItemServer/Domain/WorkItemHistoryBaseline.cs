namespace ILD.WorkItemServer.Domain;

/// <summary>
/// The Baseline save the migration that adds history seeds for every item that
/// already exists: one row per tracked field holding its value at that moment,
/// encoded as <see cref="WorkItemHistoryFields"/> encodes it, timed at the item's
/// creation and attributed to whoever the item says created it. Later changes
/// are paired against these values. Written to run on Postgres, which production
/// uses, and on SQLite, where the tests run it.
/// </summary>
public static class WorkItemHistoryBaseline
{
    public const string SeedSql = """
        INSERT INTO "WorkItemHistoryEntries"
            ("WorkItemId", "Timestamp", "SaveId", "Kind", "Username", "ActorKind", "ActorDetail", "ActorWorkItemId", "Field", "Value", "Reason")
        SELECT w."Id", w."CreatedAt", 'baseline-' || w."Id", 1, w."CreatedBy",
            CASE WHEN w."CreatedByLoopRunId" IS NOT NULL THEN 1
                 WHEN w."CreatedByChatSessionId" IS NOT NULL THEN 2 END,
            CASE WHEN w."CreatedByLoopRunId" IS NOT NULL THEN 'run ' || lower(CAST(w."CreatedByLoopRunId" AS TEXT))
                 WHEN w."CreatedByChatSessionId" IS NOT NULL THEN 'chat ' || lower(CAST(w."CreatedByChatSessionId" AS TEXT)) END,
            NULL, f."Field", f."Value", NULL
        FROM "WorkItems" w
        JOIN (
            SELECT "InternalId", 'title' AS "Field", CAST("Title" AS TEXT) AS "Value" FROM "WorkItems"
            UNION ALL SELECT "InternalId", 'description', "Description" FROM "WorkItems"
            UNION ALL SELECT "InternalId", 'status',
                CASE "Status" WHEN 0 THEN 'Backlog' WHEN 1 THEN 'WorkQueue' WHEN 2 THEN 'Ready' WHEN 3 THEN 'Running'
                    WHEN 4 THEN 'HumanFeedback' WHEN 5 THEN 'WaitingForIld' WHEN 6 THEN 'Done' END FROM "WorkItems"
            UNION ALL SELECT "InternalId", 'priority',
                CASE "Priority" WHEN 0 THEN 'Low' WHEN 1 THEN 'Medium' WHEN 2 THEN 'High' WHEN 3 THEN 'Critical' END FROM "WorkItems"
            UNION ALL SELECT "InternalId", 'tags', "TagsJson" FROM "WorkItems"
            UNION ALL SELECT "InternalId", 'dependencies', "DependenciesJson" FROM "WorkItems"
            UNION ALL SELECT "InternalId", 'repository', lower(CAST("RepositoryId" AS TEXT)) FROM "WorkItems"
            UNION ALL SELECT "InternalId", 'aiProviderOverride',
                CASE "AiProviderOverride" WHEN 0 THEN 'None' WHEN 1 THEN 'OverrideDefault' WHEN 2 THEN 'OverrideAll' END FROM "WorkItems"
            UNION ALL SELECT "InternalId", 'aiProviderOverrideId', lower(CAST("AiProviderOverrideId" AS TEXT)) FROM "WorkItems"
            UNION ALL SELECT "InternalId", 'branchNameOverride', "BranchNameOverride" FROM "WorkItems"
            UNION ALL SELECT "InternalId", 'baseBranchOverride', "BaseBranchOverride" FROM "WorkItems"
        ) f ON f."InternalId" = w."InternalId";
        """;
}
