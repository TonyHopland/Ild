using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChatUsageArchiveAppendOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChatUsages_ChatMessages_ChatMessageId",
                table: "ChatUsages");

            migrationBuilder.DropForeignKey(
                name: "FK_ChatUsages_ChatSessions_ChatSessionId",
                table: "ChatUsages");

            migrationBuilder.DropIndex(
                name: "IX_ChatUsages_ChatMessageId",
                table: "ChatUsages");

            migrationBuilder.DropIndex(
                name: "IX_ChatAnalyticsBuckets_BucketDate_ScheduleId_AiProvider",
                table: "ChatAnalyticsBuckets");

            migrationBuilder.AlterColumn<string>(
                name: "ScheduleName",
                table: "ChatAnalyticsBuckets",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(120)",
                oldMaxLength: 120);

            migrationBuilder.AlterColumn<Guid>(
                name: "ScheduleId",
                table: "ChatAnalyticsBuckets",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ScheduleName",
                table: "ChatAnalyticsBuckets",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(120)",
                oldMaxLength: 120,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ScheduleId",
                table: "ChatAnalyticsBuckets",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatUsages_ChatMessageId",
                table: "ChatUsages",
                column: "ChatMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatAnalyticsBuckets_BucketDate_ScheduleId_AiProvider",
                table: "ChatAnalyticsBuckets",
                columns: new[] { "BucketDate", "ScheduleId", "AiProvider" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ChatUsages_ChatMessages_ChatMessageId",
                table: "ChatUsages",
                column: "ChatMessageId",
                principalTable: "ChatMessages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ChatUsages_ChatSessions_ChatSessionId",
                table: "ChatUsages",
                column: "ChatSessionId",
                principalTable: "ChatSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
