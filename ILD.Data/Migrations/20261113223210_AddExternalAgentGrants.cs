using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalAgentGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByExternalGrantId",
                table: "LoopTemplateProposals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByExternalGrantId",
                table: "HumanInputProposals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByExternalGrantId",
                table: "GlobalVariableProposals",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExternalAgentGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedFrom = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalAgentGrants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExternalAgentGrantActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    From = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalAgentGrantActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalAgentGrantActivities_ExternalAgentGrants_GrantId",
                        column: x => x.GrantId,
                        principalTable: "ExternalAgentGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoopTemplateProposals_CreatedByExternalGrantId",
                table: "LoopTemplateProposals",
                column: "CreatedByExternalGrantId");

            migrationBuilder.CreateIndex(
                name: "IX_LoopTemplateProposals_Grant_Pending",
                table: "LoopTemplateProposals",
                columns: new[] { "CreatedByExternalGrantId", "LoopTemplateId" },
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_HumanInputProposals_CreatedByExternalGrantId",
                table: "HumanInputProposals",
                column: "CreatedByExternalGrantId");

            migrationBuilder.CreateIndex(
                name: "IX_HumanInputProposals_Grant_Pending",
                table: "HumanInputProposals",
                columns: new[] { "CreatedByExternalGrantId", "WorkItemId" },
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_GlobalVariableProposals_CreatedByExternalGrantId",
                table: "GlobalVariableProposals",
                column: "CreatedByExternalGrantId");

            migrationBuilder.CreateIndex(
                name: "IX_GlobalVariableProposals_Grant_Pending",
                table: "GlobalVariableProposals",
                columns: new[] { "CreatedByExternalGrantId", "NormalizedName" },
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAgentGrantActivities_GrantId_At",
                table: "ExternalAgentGrantActivities",
                columns: new[] { "GrantId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAgentGrants_ExpiresAt",
                table: "ExternalAgentGrants",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalAgentGrants_TokenHash",
                table: "ExternalAgentGrants",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalAgentGrantActivities");

            migrationBuilder.DropTable(
                name: "ExternalAgentGrants");

            migrationBuilder.DropIndex(
                name: "IX_LoopTemplateProposals_CreatedByExternalGrantId",
                table: "LoopTemplateProposals");

            migrationBuilder.DropIndex(
                name: "IX_LoopTemplateProposals_Grant_Pending",
                table: "LoopTemplateProposals");

            migrationBuilder.DropIndex(
                name: "IX_HumanInputProposals_CreatedByExternalGrantId",
                table: "HumanInputProposals");

            migrationBuilder.DropIndex(
                name: "IX_HumanInputProposals_Grant_Pending",
                table: "HumanInputProposals");

            migrationBuilder.DropIndex(
                name: "IX_GlobalVariableProposals_CreatedByExternalGrantId",
                table: "GlobalVariableProposals");

            migrationBuilder.DropIndex(
                name: "IX_GlobalVariableProposals_Grant_Pending",
                table: "GlobalVariableProposals");

            migrationBuilder.DropColumn(
                name: "CreatedByExternalGrantId",
                table: "LoopTemplateProposals");

            migrationBuilder.DropColumn(
                name: "CreatedByExternalGrantId",
                table: "HumanInputProposals");

            migrationBuilder.DropColumn(
                name: "CreatedByExternalGrantId",
                table: "GlobalVariableProposals");
        }
    }
}
