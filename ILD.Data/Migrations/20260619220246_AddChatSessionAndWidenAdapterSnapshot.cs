using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatSessionAndWidenAdapterSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_AdapterSessionSnapshots",
                table: "AdapterSessionSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_AdapterSessionSnapshots_LoopRunId_AdapterName",
                table: "AdapterSessionSnapshots");

            migrationBuilder.AlterColumn<Guid>(
                name: "LoopRunId",
                table: "AdapterSessionSnapshots",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "Id",
                table: "AdapterSessionSnapshots",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "ChatSessionId",
                table: "AdapterSessionSnapshots",
                type: "uuid",
                nullable: true);

            // Give existing rows a unique surrogate id before it becomes the PK —
            // the AddColumn above seeds them all with the same empty guid, which
            // would collide. Postgres-only: this migration only ever runs against
            // Postgres (tests use EnsureCreated on SQLite, never migrations).
            migrationBuilder.Sql("UPDATE \"AdapterSessionSnapshots\" SET \"Id\" = gen_random_uuid();");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AdapterSessionSnapshots",
                table: "AdapterSessionSnapshots",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "ChatSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AiProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ToolAllowlistCsv = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ScratchPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    CurrentSessionId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChatMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Interrupted = table.Column<bool>(type: "boolean", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatMessages_ChatSessions_ChatSessionId",
                        column: x => x.ChatSessionId,
                        principalTable: "ChatSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdapterSessionSnapshots_ChatSessionId_AdapterName_SessionId",
                table: "AdapterSessionSnapshots",
                columns: new[] { "ChatSessionId", "AdapterName", "SessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdapterSessionSnapshots_LoopRunId_AdapterName_SessionId",
                table: "AdapterSessionSnapshots",
                columns: new[] { "LoopRunId", "AdapterName", "SessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ChatSessionId_Sequence",
                table: "ChatMessages",
                columns: new[] { "ChatSessionId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatSessions_UserId",
                table: "ChatSessions",
                column: "UserId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AdapterSessionSnapshots_ChatSessions_ChatSessionId",
                table: "AdapterSessionSnapshots",
                column: "ChatSessionId",
                principalTable: "ChatSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AdapterSessionSnapshots_ChatSessions_ChatSessionId",
                table: "AdapterSessionSnapshots");

            migrationBuilder.DropTable(
                name: "ChatMessages");

            migrationBuilder.DropTable(
                name: "ChatSessions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AdapterSessionSnapshots",
                table: "AdapterSessionSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_AdapterSessionSnapshots_ChatSessionId_AdapterName_SessionId",
                table: "AdapterSessionSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_AdapterSessionSnapshots_LoopRunId_AdapterName_SessionId",
                table: "AdapterSessionSnapshots");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "AdapterSessionSnapshots");

            migrationBuilder.DropColumn(
                name: "ChatSessionId",
                table: "AdapterSessionSnapshots");

            migrationBuilder.AlterColumn<Guid>(
                name: "LoopRunId",
                table: "AdapterSessionSnapshots",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_AdapterSessionSnapshots",
                table: "AdapterSessionSnapshots",
                columns: new[] { "LoopRunId", "AdapterName", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_AdapterSessionSnapshots_LoopRunId_AdapterName",
                table: "AdapterSessionSnapshots",
                columns: new[] { "LoopRunId", "AdapterName" });
        }
    }
}
