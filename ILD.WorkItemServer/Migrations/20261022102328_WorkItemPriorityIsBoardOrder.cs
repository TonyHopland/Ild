using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.WorkItemServer.Migrations
{
    /// <inheritdoc />
    public partial class WorkItemPriorityIsBoardOrder : Migration
    {
        // Numbers every item in the order the board showed before: newest first
        // from 0, and the later-inserted item first when two were created at the
        // same instant, so every item gets its own value.
        private const string BoardOrderSql = """
            UPDATE "WorkItems" SET "Priority" = ranked."Rank"
            FROM (
                SELECT "InternalId", ROW_NUMBER() OVER (ORDER BY "CreatedAt" DESC, "InternalId" DESC) - 1 AS "Rank"
                FROM "WorkItems") AS ranked
            WHERE "WorkItems"."InternalId" = ranked."InternalId"
            """;

        // The old enum's Medium, so every item reads as a valid priority again.
        private const string ResetToMediumSql = """
            UPDATE "WorkItems" SET "Priority" = 1
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<double>(
                name: "Priority",
                table: "WorkItems",
                type: "double precision",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.Sql(BoardOrderSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ResetToMediumSql);

            migrationBuilder.AlterColumn<int>(
                name: "Priority",
                table: "WorkItems",
                type: "integer",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "double precision");
        }
    }
}
