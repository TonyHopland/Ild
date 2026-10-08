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
                name: "ChatScheduleId",
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
                    LatestChatSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    NextFireAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PendingSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatSchedules_ChatSessions_LatestChatSessionId",
                        column: x => x.LatestChatSessionId,
                        principalTable: "ChatSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ChatScheduleFirings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ScheduledFor = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ChatSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    TurnId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatScheduleFirings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatScheduleFirings_ChatSchedules_ChatScheduleId",
                        column: x => x.ChatScheduleId,
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

            migrationBuilder.CreateTable(
                name: "ChatScheduleFiringWorkItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatScheduleFiringId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatScheduleFiringWorkItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatScheduleFiringWorkItems_ChatScheduleFirings_ChatSchedul~",
                        column: x => x.ChatScheduleFiringId,
                        principalTable: "ChatScheduleFirings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatSessions_ChatScheduleId",
                table: "ChatSessions",
                column: "ChatScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatScheduleFirings_ChatScheduleId_Number",
                table: "ChatScheduleFirings",
                columns: new[] { "ChatScheduleId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatScheduleFirings_ChatSessionId",
                table: "ChatScheduleFirings",
                column: "ChatSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatScheduleFirings_Outcome",
                table: "ChatScheduleFirings",
                column: "Outcome");

            migrationBuilder.CreateIndex(
                name: "IX_ChatScheduleFirings_TurnId",
                table: "ChatScheduleFirings",
                column: "TurnId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatScheduleFiringWorkItems_ChatScheduleFiringId",
                table: "ChatScheduleFiringWorkItems",
                column: "ChatScheduleFiringId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatSchedules_LatestChatSessionId",
                table: "ChatSchedules",
                column: "LatestChatSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatSchedules_NextFireAt",
                table: "ChatSchedules",
                column: "NextFireAt");

            migrationBuilder.CreateIndex(
                name: "IX_ChatSchedules_UserId",
                table: "ChatSchedules",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_ChatSessions_ChatSchedules_ChatScheduleId",
                table: "ChatSessions",
                column: "ChatScheduleId",
                principalTable: "ChatSchedules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChatSessions_ChatSchedules_ChatScheduleId",
                table: "ChatSessions");

            migrationBuilder.DropTable(
                name: "ChatScheduleFiringWorkItems");

            migrationBuilder.DropTable(
                name: "ChatScheduleFirings");

            migrationBuilder.DropTable(
                name: "ChatSchedules");

            migrationBuilder.DropIndex(
                name: "IX_ChatSessions_ChatScheduleId",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "ChatScheduleId",
                table: "ChatSessions");
        }
    }
}
