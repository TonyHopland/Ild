using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ApiKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    Config = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiProviders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoopTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecoveryPolicy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    MaxNodeExecutions = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxWallClockHours = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoopTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RemoteProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ApiKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    WebhookSecret = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    WorkItemServerUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    WorkItemApiKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    PollIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    GraceIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxConcurrentWorkItems = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteProviders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SessionToken = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoopTemplateVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LoopTemplateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VersionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoopTemplateVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoopTemplateVersions_LoopTemplates_LoopTemplateId",
                        column: x => x.LoopTemplateId,
                        principalTable: "LoopTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Repositories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RemoteProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CloneUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    DefaultBranch = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    WorktreesPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    DefaultIntakeStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repositories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Repositories_RemoteProviders_RemoteProviderId",
                        column: x => x.RemoteProviderId,
                        principalTable: "RemoteProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LoopNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LoopTemplateVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NodeType = table.Column<int>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Config = table.Column<string>(type: "TEXT", nullable: true),
                    MaxRetries = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeoutSeconds = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoopNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoopNodes_LoopTemplateVersions_LoopTemplateVersionId",
                        column: x => x.LoopTemplateVersionId,
                        principalTable: "LoopTemplateVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LoopRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LoopTemplateVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RecoveryPolicy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IsPaused = table.Column<bool>(type: "INTEGER", nullable: false),
                    NodeExecutionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextEventSeq = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CurrentNodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SessionsJson = table.Column<string>(type: "TEXT", nullable: true),
                    WorktreePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    BranchName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PrUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    IsPrMerged = table.Column<bool>(type: "INTEGER", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedByLoopRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    HumanFeedbackReason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoopRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoopRuns_LoopTemplateVersions_LoopTemplateVersionId",
                        column: x => x.LoopTemplateVersionId,
                        principalTable: "LoopTemplateVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LoopNodeEdges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EdgeType = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxTraversals = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoopNodeEdges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoopNodeEdges_LoopNodes_SourceNodeId",
                        column: x => x.SourceNodeId,
                        principalTable: "LoopNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LoopNodeEdges_LoopNodes_TargetNodeId",
                        column: x => x.TargetNodeId,
                        principalTable: "LoopNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EventLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LoopRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    EventType = table.Column<int>(type: "INTEGER", nullable: false),
                    NodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RunNodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PayloadPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Data = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventLogs_LoopRuns_LoopRunId",
                        column: x => x.LoopRunId,
                        principalTable: "LoopRuns",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "LoopRunNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LoopRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LoopNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NodeLabel = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Output = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoopRunNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoopRunNodes_LoopNodes_LoopNodeId",
                        column: x => x.LoopNodeId,
                        principalTable: "LoopNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LoopRunNodes_LoopRuns_LoopRunId",
                        column: x => x.LoopRunId,
                        principalTable: "LoopRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LoopRunEdgeTraversals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LoopRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EdgeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TraversalCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoopRunEdgeTraversals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoopRunEdgeTraversals_LoopNodeEdges_EdgeId",
                        column: x => x.EdgeId,
                        principalTable: "LoopNodeEdges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LoopRunEdgeTraversals_LoopRuns_LoopRunId",
                        column: x => x.LoopRunId,
                        principalTable: "LoopRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiProviders_Name",
                table: "AiProviders",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_EventLogs_LoopRunId_Sequence",
                table: "EventLogs",
                columns: new[] { "LoopRunId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_EventLogs_Timestamp",
                table: "EventLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_LoopNodeEdges_SourceNodeId_TargetNodeId",
                table: "LoopNodeEdges",
                columns: new[] { "SourceNodeId", "TargetNodeId" });

            migrationBuilder.CreateIndex(
                name: "IX_LoopNodeEdges_TargetNodeId",
                table: "LoopNodeEdges",
                column: "TargetNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_LoopNodes_LoopTemplateVersionId",
                table: "LoopNodes",
                column: "LoopTemplateVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_LoopRunEdgeTraversals_EdgeId",
                table: "LoopRunEdgeTraversals",
                column: "EdgeId");

            migrationBuilder.CreateIndex(
                name: "IX_LoopRunEdgeTraversals_LoopRunId_EdgeId",
                table: "LoopRunEdgeTraversals",
                columns: new[] { "LoopRunId", "EdgeId" });

            migrationBuilder.CreateIndex(
                name: "IX_LoopRunNodes_LoopNodeId",
                table: "LoopRunNodes",
                column: "LoopNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_LoopRunNodes_LoopRunId",
                table: "LoopRunNodes",
                column: "LoopRunId");

            migrationBuilder.CreateIndex(
                name: "IX_LoopRunNodes_Status",
                table: "LoopRunNodes",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_LoopRuns_LoopTemplateVersionId",
                table: "LoopRuns",
                column: "LoopTemplateVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_LoopRuns_PrUrl",
                table: "LoopRuns",
                column: "PrUrl");

            migrationBuilder.CreateIndex(
                name: "IX_LoopRuns_Status",
                table: "LoopRuns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_LoopRuns_WorkItemId",
                table: "LoopRuns",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_LoopTemplates_Name",
                table: "LoopTemplates",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_LoopTemplateVersions_LoopTemplateId_VersionNumber",
                table: "LoopTemplateVersions",
                columns: new[] { "LoopTemplateId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RemoteProviders_Name",
                table: "RemoteProviders",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_Name",
                table: "Repositories",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_RemoteProviderId",
                table: "Repositories",
                column: "RemoteProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_SessionToken",
                table: "Users",
                column: "SessionToken");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiProviders");

            migrationBuilder.DropTable(
                name: "EventLogs");

            migrationBuilder.DropTable(
                name: "LoopRunEdgeTraversals");

            migrationBuilder.DropTable(
                name: "LoopRunNodes");

            migrationBuilder.DropTable(
                name: "Repositories");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "LoopNodeEdges");

            migrationBuilder.DropTable(
                name: "LoopRuns");

            migrationBuilder.DropTable(
                name: "RemoteProviders");

            migrationBuilder.DropTable(
                name: "LoopNodes");

            migrationBuilder.DropTable(
                name: "LoopTemplateVersions");

            migrationBuilder.DropTable(
                name: "LoopTemplates");
        }
    }
}
