using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatUsageAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChatAnalyticsBuckets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BucketDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AiProvider = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TurnCount = table.Column<int>(type: "integer", nullable: false),
                    TotalInputTokens = table.Column<long>(type: "bigint", nullable: false),
                    TotalOutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    TotalCostUsd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatAnalyticsBuckets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChatUsages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AiProvider = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    CostUsd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    ScheduleId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScheduleName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatUsages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatUsages_ChatMessages_ChatMessageId",
                        column: x => x.ChatMessageId,
                        principalTable: "ChatMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatUsages_ChatSessions_ChatSessionId",
                        column: x => x.ChatSessionId,
                        principalTable: "ChatSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatAnalyticsBuckets_BucketDate",
                table: "ChatAnalyticsBuckets",
                column: "BucketDate");

            migrationBuilder.CreateIndex(
                name: "IX_ChatAnalyticsBuckets_BucketDate_ScheduleId_AiProvider",
                table: "ChatAnalyticsBuckets",
                columns: new[] { "BucketDate", "ScheduleId", "AiProvider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatUsages_ChatMessageId",
                table: "ChatUsages",
                column: "ChatMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatUsages_ChatSessionId",
                table: "ChatUsages",
                column: "ChatSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatUsages_CreatedAt",
                table: "ChatUsages",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatAnalyticsBuckets");

            migrationBuilder.DropTable(
                name: "ChatUsages");
        }
    }
}
