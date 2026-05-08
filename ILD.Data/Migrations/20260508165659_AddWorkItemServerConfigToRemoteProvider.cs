using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemServerConfigToRemoteProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GraceIntervalSeconds",
                table: "RemoteProviders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxConcurrentWorkItems",
                table: "RemoteProviders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PollIntervalSeconds",
                table: "RemoteProviders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "WorkItemApiKey",
                table: "RemoteProviders",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkItemServerUrl",
                table: "RemoteProviders",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GraceIntervalSeconds",
                table: "RemoteProviders");

            migrationBuilder.DropColumn(
                name: "MaxConcurrentWorkItems",
                table: "RemoteProviders");

            migrationBuilder.DropColumn(
                name: "PollIntervalSeconds",
                table: "RemoteProviders");

            migrationBuilder.DropColumn(
                name: "WorkItemApiKey",
                table: "RemoteProviders");

            migrationBuilder.DropColumn(
                name: "WorkItemServerUrl",
                table: "RemoteProviders");
        }
    }
}
