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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ApiKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    Parallelism = table.Column<int>(type: "integer", nullable: false),
                    Config = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiProviders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoopTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    RecoveryPolicy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    MaxNodeExecutions = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoopTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RemoteProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ApiKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    WebhookSecret = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    WorkItemServerUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    WorkItemApiKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    PollIntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    GraceIntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxConcurrentWorkItems = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteProviders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SessionToken = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoopTemplateVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LoopTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RemoteProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    CloneUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    DefaultBranch = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    WorktreesPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    DefaultIntakeStatus = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LoopTemplateVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeType = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Config = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<string>(type: "text", nullable: false),
                    LoopTemplateVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RecoveryPolicy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsPaused = table.Column<bool>(type: "boolean", nullable: false),
                    NodeExecutionCount = table.Column<int>(type: "integer", nullable: false),
                    NextEventSeq = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CurrentNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorktreePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    BranchName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PrUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    IsPrMerged = table.Column<bool>(type: "boolean", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByLoopRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    HumanFeedbackReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PreviousNodeOutput = table.Column<string>(type: "text", nullable: true),
                    ExternalActionResult = table.Column<string>(type: "text", nullable: true),
                    ExternalActionResultRejected = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceNodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetNodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    EdgeType = table.Column<int>(type: "integer", nullable: false),
                    MaxTraversals = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
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
                name: "AdapterSessionSnapshots",
                columns: table => new
                {
                    LoopRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdapterName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SessionId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SessionJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdapterSessionSnapshots", x => new { x.LoopRunId, x.AdapterName, x.SessionId });
                    table.ForeignKey(
                        name: "FK_AdapterSessionSnapshots_LoopRuns_LoopRunId",
                        column: x => x.LoopRunId,
                        principalTable: "LoopRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EventLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LoopRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    RunNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PayloadPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Data = table.Column<string>(type: "text", nullable: true)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LoopRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoopNodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeLabel = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Output = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    EffectiveInput = table.Column<string>(type: "text", nullable: true),
                    PreviousNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "LoopRunSessionBindings",
                columns: table => new
                {
                    LoopRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdapterName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PlaceholderId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SessionId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoopRunSessionBindings", x => new { x.LoopRunId, x.AdapterName, x.PlaceholderId });
                    table.ForeignKey(
                        name: "FK_LoopRunSessionBindings_LoopRuns_LoopRunId",
                        column: x => x.LoopRunId,
                        principalTable: "LoopRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdapterSessionSnapshots_LoopRunId_AdapterName",
                table: "AdapterSessionSnapshots",
                columns: new[] { "LoopRunId", "AdapterName" });

            migrationBuilder.CreateIndex(
                name: "IX_AiProviders_Name",
                table: "AiProviders",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_AppSettings_Key",
                table: "AppSettings",
                column: "Key",
                unique: true);

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
                name: "IX_LoopRunSessionBindings_LoopRunId_AdapterName_SessionId",
                table: "LoopRunSessionBindings",
                columns: new[] { "LoopRunId", "AdapterName", "SessionId" });

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
                name: "AdapterSessionSnapshots");

            migrationBuilder.DropTable(
                name: "AiProviders");

            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "EventLogs");

            migrationBuilder.DropTable(
                name: "LoopNodeEdges");

            migrationBuilder.DropTable(
                name: "LoopRunNodes");

            migrationBuilder.DropTable(
                name: "LoopRunSessionBindings");

            migrationBuilder.DropTable(
                name: "Repositories");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "LoopNodes");

            migrationBuilder.DropTable(
                name: "LoopRuns");

            migrationBuilder.DropTable(
                name: "RemoteProviders");

            migrationBuilder.DropTable(
                name: "LoopTemplateVersions");

            migrationBuilder.DropTable(
                name: "LoopTemplates");
        }
    }
}
