using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventLogPosition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Position",
                table: "EventLogs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Data migration: number every event in its run's order, so the
            // identity key that replaces Sequence keeps the timeline as it was.
            // Sequenced rows keep their Sequence order. Rows written with
            // Sequence 0 (PR, webhook and review events) slot in by time, after
            // the last sequenced row of their run that is not later than they are.
            migrationBuilder.Sql(@"
WITH keyed AS (
    SELECT e.""Id"", e.""LoopRunId"", e.""Timestamp"",
        CASE WHEN e.""Sequence"" > 0 THEN e.""Sequence""
            ELSE COALESCE((
                SELECT MAX(s.""Sequence"") FROM ""EventLogs"" s
                WHERE s.""LoopRunId"" = e.""LoopRunId"" AND s.""Sequence"" > 0
                    AND s.""Timestamp"" <= e.""Timestamp""), 0)
        END AS slot,
        CASE WHEN e.""Sequence"" > 0 THEN 0 ELSE 1 END AS unsequenced
    FROM ""EventLogs"" e
), numbered AS (
    SELECT ""Id"", ROW_NUMBER() OVER (
        ORDER BY ""LoopRunId"", slot, unsequenced, ""Timestamp"", ""Id"") AS position
    FROM keyed
)
UPDATE ""EventLogs"" e SET ""Position"" = n.position
FROM numbered n WHERE e.""Id"" = n.""Id"";");

            // Data migration: a work item has at most one live run (Running or
            // WaitingHuman), which the next migration makes a unique index. Any
            // duplicates a bug let through are cancelled, keeping the newest.
            migrationBuilder.Sql(@"
UPDATE ""LoopRuns"" r SET ""Status"" = 3, ""CompletedAt"" = now()
WHERE r.""Status"" IN (0, 4)
    AND EXISTS (
        SELECT 1 FROM ""LoopRuns"" n
        WHERE n.""WorkItemId"" = r.""WorkItemId"" AND n.""Status"" IN (0, 4) AND n.""Id"" <> r.""Id""
            AND (COALESCE(n.""StartedAt"", n.""CreatedAt""), n.""Id"")
                > (COALESCE(r.""StartedAt"", r.""CreatedAt""), r.""Id""));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Position",
                table: "EventLogs");
        }
    }
}
