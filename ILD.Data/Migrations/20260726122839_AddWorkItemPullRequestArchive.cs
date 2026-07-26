using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemPullRequestArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkItemPullRequestRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    LoopRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    Merged = table.Column<bool>(type: "boolean", nullable: false),
                    PrSnapshot = table.Column<string>(type: "text", nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemPullRequestRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemPullRequestRecords_WorkItemId",
                table: "WorkItemPullRequestRecords",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemPullRequestRecords_WorkItemId_Url",
                table: "WorkItemPullRequestRecords",
                columns: new[] { "WorkItemId", "Url" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkItemPullRequestRecords");
        }
    }
}
