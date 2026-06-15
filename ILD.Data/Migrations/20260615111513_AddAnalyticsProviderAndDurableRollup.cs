using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalyticsProviderAndDurableRollup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiProvider",
                table: "LoopRunNodes",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LoopRunAnalyticsBuckets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BucketDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LoopTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AiProvider = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RunCount = table.Column<int>(type: "integer", nullable: false),
                    CompletedRuns = table.Column<int>(type: "integer", nullable: false),
                    FailedRuns = table.Column<int>(type: "integer", nullable: false),
                    CancelledRuns = table.Column<int>(type: "integer", nullable: false),
                    NodeCount = table.Column<int>(type: "integer", nullable: false),
                    TotalNodeSeconds = table.Column<double>(type: "double precision", nullable: false),
                    OnFailureRoutings = table.Column<int>(type: "integer", nullable: false),
                    RejectRoutings = table.Column<int>(type: "integer", nullable: false),
                    FeedbackCount = table.Column<int>(type: "integer", nullable: false),
                    TotalFeedbackSeconds = table.Column<double>(type: "double precision", nullable: false),
                    TotalInputTokens = table.Column<long>(type: "bigint", nullable: false),
                    TotalOutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    TotalCostUsd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoopRunAnalyticsBuckets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoopRunAnalyticsBuckets_BucketDate",
                table: "LoopRunAnalyticsBuckets",
                column: "BucketDate");

            migrationBuilder.CreateIndex(
                name: "IX_LoopRunAnalyticsBuckets_BucketDate_LoopTemplateId_AiProvider",
                table: "LoopRunAnalyticsBuckets",
                columns: new[] { "BucketDate", "LoopTemplateId", "AiProvider" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoopRunAnalyticsBuckets");

            migrationBuilder.DropColumn(
                name: "AiProvider",
                table: "LoopRunNodes");
        }
    }
}
