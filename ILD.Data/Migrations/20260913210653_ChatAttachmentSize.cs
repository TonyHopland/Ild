using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChatAttachmentSize : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SizeBytes",
                table: "ChatAttachments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Rows that predate the column would otherwise sit at the default of
            // zero and render as "0 B" in every reopened transcript — the column
            // exists precisely because the size cannot be measured once the blob
            // is left unloaded, so it is measured here, once, while in SQL reach.
            migrationBuilder.Sql(
                @"UPDATE ""ChatAttachments"" SET ""SizeBytes"" = octet_length(""Content"")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SizeBytes",
                table: "ChatAttachments");
        }
    }
}
