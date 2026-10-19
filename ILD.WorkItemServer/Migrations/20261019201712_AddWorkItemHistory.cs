using System;
using ILD.WorkItemServer.Domain;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ILD.WorkItemServer.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkItemHistoryEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkItemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SaveId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ActorKind = table.Column<int>(type: "integer", nullable: true),
                    ActorDetail = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ActorWorkItemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Field = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemHistoryEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemHistoryEntries_WorkItemId_Field_Timestamp",
                table: "WorkItemHistoryEntries",
                columns: new[] { "WorkItemId", "Field", "Timestamp" });

            migrationBuilder.Sql(WorkItemHistoryBaseline.SeedSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkItemHistoryEntries");
        }
    }
}
