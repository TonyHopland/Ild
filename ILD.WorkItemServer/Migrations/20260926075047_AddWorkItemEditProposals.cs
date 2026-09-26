using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.WorkItemServer.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemEditProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkItemEditProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<int>(type: "integer", nullable: false),
                    ProposedTitle = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ProposedDescription = table.Column<string>(type: "text", nullable: true),
                    ProposedTagsJson = table.Column<string>(type: "text", nullable: true),
                    ProposedBranchNameOverride = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ProposedBaseBranchOverride = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SnapshotTitle = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SnapshotDescription = table.Column<string>(type: "text", nullable: true),
                    SnapshotTagsJson = table.Column<string>(type: "text", nullable: false),
                    SnapshotBranchNameOverride = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SnapshotBaseBranchOverride = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedByLoopRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByChatSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RejectionReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionDeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemEditProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItemEditProposals_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "InternalId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemEditProposals_CreatedByChatSessionId",
                table: "WorkItemEditProposals",
                column: "CreatedByChatSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemEditProposals_WorkItemId_Status",
                table: "WorkItemEditProposals",
                columns: new[] { "WorkItemId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkItemEditProposals");
        }
    }
}
