using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class SplitSessionSnapshotContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "SessionJson",
                table: "AdapterSessionSnapshots",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<long>(
                name: "ByteLength",
                table: "AdapterSessionSnapshots",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "ChunkCount",
                table: "AdapterSessionSnapshots",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Compression",
                table: "AdapterSessionSnapshots",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "EventCount",
                table: "AdapterSessionSnapshots",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Format",
                table: "AdapterSessionSnapshots",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TailHash",
                table: "AdapterSessionSnapshots",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AdapterSessionSnapshotChunks",
                columns: table => new
                {
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seq = table.Column<int>(type: "integer", nullable: false),
                    RawOffset = table.Column<long>(type: "bigint", nullable: false),
                    RawLength = table.Column<int>(type: "integer", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdapterSessionSnapshotChunks", x => new { x.SnapshotId, x.Seq });
                    table.ForeignKey(
                        name: "FK_AdapterSessionSnapshotChunks_AdapterSessionSnapshots_Snapsh~",
                        column: x => x.SnapshotId,
                        principalTable: "AdapterSessionSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdapterSessionSnapshotChunks");

            migrationBuilder.DropColumn(
                name: "ByteLength",
                table: "AdapterSessionSnapshots");

            migrationBuilder.DropColumn(
                name: "ChunkCount",
                table: "AdapterSessionSnapshots");

            migrationBuilder.DropColumn(
                name: "Compression",
                table: "AdapterSessionSnapshots");

            migrationBuilder.DropColumn(
                name: "EventCount",
                table: "AdapterSessionSnapshots");

            migrationBuilder.DropColumn(
                name: "Format",
                table: "AdapterSessionSnapshots");

            migrationBuilder.DropColumn(
                name: "TailHash",
                table: "AdapterSessionSnapshots");

            migrationBuilder.AlterColumn<string>(
                name: "SessionJson",
                table: "AdapterSessionSnapshots",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
