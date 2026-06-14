using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomEdgeNameAndExternalActionEdgeName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalActionEdgeName",
                table: "LoopRuns",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "LoopNodeEdges",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            // Data migration: the historical OnRespond role (EdgeType = 2) is now
            // the generic named-custom role. Existing respond edges become the
            // custom edge named "Respond" so they keep routing identically.
            migrationBuilder.Sql(
                "UPDATE \"LoopNodeEdges\" SET \"Name\" = 'Respond' WHERE \"EdgeType\" = 2 AND \"Name\" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalActionEdgeName",
                table: "LoopRuns");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "LoopNodeEdges");
        }
    }
}
