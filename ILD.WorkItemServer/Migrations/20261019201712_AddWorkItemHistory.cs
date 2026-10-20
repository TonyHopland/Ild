using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ILD.WorkItemServer.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemHistory : Migration
    {
        // One Baseline save per existing item: each tracked field's value when
        // history began, encoded as WorkItemHistoryFields encodes it, timed at the
        // item's creation and attributed to whoever the item says created it. Frozen
        // here, as a migration must be: a field tracked later is not part of it.
        private const string BaselineSeedSql = """
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

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkItemHistoryEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkItemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SaveId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ActorKind = table.Column<int>(type: "integer", nullable: true),
                    ActorDetail = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ActorWorkItemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Field = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemHistoryEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemHistoryEntries_WorkItemId_Field_Timestamp",
                table: "WorkItemHistoryEntries",
                columns: new[] { "WorkItemId", "Field", "Timestamp" });

            migrationBuilder.Sql(BaselineSeedSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkItemHistoryEntries");
        }
    }
}
