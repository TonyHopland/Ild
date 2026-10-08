using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ScheduleId",
                table: "ChatSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChatSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Prompt = table.Column<string>(type: "text", nullable: false),
                    AiTag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CronExpression = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    RepositoryScope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RepositoryIdsCsv = table.Column<string>(type: "text", nullable: false),
                    ContinueSession = table.Column<bool>(type: "boolean", nullable: false),
                    ContinueChatSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    NextFireAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PendingSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatSchedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChatScheduleFirings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChatSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    TurnId = table.Column<Guid>(type: "uuid", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatScheduleFirings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatScheduleFirings_ChatSchedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "ChatSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatScheduleFirings_ChatSessions_ChatSessionId",
                        column: x => x.ChatSessionId,
                        principalTable: "ChatSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatSessions_ScheduleId",
                table: "ChatSessions",
                column: "ScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatScheduleFirings_ChatSessionId",
                table: "ChatScheduleFirings",
                column: "ChatSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatScheduleFirings_Outcome",
                table: "ChatScheduleFirings",
                column: "Outcome");

            migrationBuilder.CreateIndex(
                name: "IX_ChatScheduleFirings_ScheduleId_Sequence",
                table: "ChatScheduleFirings",
                columns: new[] { "ScheduleId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatScheduleFirings_TurnId",
                table: "ChatScheduleFirings",
                column: "TurnId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatSchedules_Enabled_NextFireAt",
                table: "ChatSchedules",
                columns: new[] { "Enabled", "NextFireAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatSchedules_UserId",
                table: "ChatSchedules",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_ChatSessions_ChatSchedules_ScheduleId",
                table: "ChatSessions",
                column: "ScheduleId",
                principalTable: "ChatSchedules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChatSessions_ChatSchedules_ScheduleId",
                table: "ChatSessions");

            migrationBuilder.DropTable(
                name: "ChatScheduleFirings");

            migrationBuilder.DropTable(
                name: "ChatSchedules");

            migrationBuilder.DropIndex(
                name: "IX_ChatSessions_ScheduleId",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "ScheduleId",
                table: "ChatSessions");
        }
    }
}
