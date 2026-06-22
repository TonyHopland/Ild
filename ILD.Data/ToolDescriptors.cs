using ILD.Data.DTOs;

namespace ILD.Data;

/// <summary>
/// Single source of truth for all ILD agent-scoped API tools.
/// Referenced by the Pi extension generator to avoid duplicating tool definitions.
/// </summary>
public static class ToolDescriptors
{
    // -- Read tools --

    public static readonly ToolDescriptor ListWorkItems = new()
    {
        Name = "ild_list_workitems",
        Label = "List Work Items",
        Description = "List work items, optionally filtered. Status is one of Backlog, WorkQueue, Ready, Running, HumanFeedback, Done.",
        EndpointPath = "api/v1/agent/workitems",
        HttpMethod = HttpMethod.Get,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "status", Description = "Filter by status: Backlog, WorkQueue, Ready, Running, HumanFeedback, Done.", TsType = "string", IsOptional = true },
            new() { Name = "repositoryId", Description = "Filter by repository GUID.", TsType = "string", IsOptional = true },
            new() { Name = "createdByLoopRunId", Description = "Filter by originating LoopRun GUID.", TsType = "string", IsOptional = true },
            new() { Name = "skip", Description = "Pagination skip (default 0).", TsType = "number", IsOptional = true },
            new() { Name = "take", Description = "Pagination take (default 100, max 500).", TsType = "number", IsOptional = true },
        },
    };

    public static readonly ToolDescriptor GetWorkItem = new()
    {
        Name = "ild_get_workitem",
        Label = "Get Work Item",
        Description = "Get a single work item by id, including its dependencies.",
        EndpointPath = "api/v1/agent/workitems/{id}",
        HttpMethod = HttpMethod.Get,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "id", Description = "Work item GUID.", TsType = "string" },
        },
    };

    public static readonly ToolDescriptor ListRepositories = new()
    {
        Name = "ild_list_repositories",
        Label = "List Repositories",
        Description = "List repositories the agent can attach a work item to.",
        EndpointPath = "api/v1/agent/repositories",
        HttpMethod = HttpMethod.Get,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "skip", Description = "Pagination skip (default 0).", TsType = "number", IsOptional = true },
            new() { Name = "take", Description = "Pagination take (default 100, max 500).", TsType = "number", IsOptional = true },
        },
    };

    public static readonly ToolDescriptor ListLoopTemplates = new()
    {
        Name = "ild_list_loop_templates",
        Label = "List Loop Templates",
        Description = "List loop templates available for new work items.",
        EndpointPath = "api/v1/agent/loop-templates",
        HttpMethod = HttpMethod.Get,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "skip", Description = "Pagination skip (default 0).", TsType = "number", IsOptional = true },
            new() { Name = "take", Description = "Pagination take (default 100, max 500).", TsType = "number", IsOptional = true },
            new() { Name = "includeArchived", Description = "Include archived templates (default false).", TsType = "boolean", IsOptional = true },
        },
    };

    public static readonly ToolDescriptor ListLoopRuns = new()
    {
        Name = "ild_list_loop_runs",
        Label = "List Loop Runs",
        Description = "List loop runs. Pass workItemId to scope to a specific work item.",
        EndpointPath = "api/v1/agent/loop-runs",
        HttpMethod = HttpMethod.Get,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "workItemId", Description = "WorkItem GUID to scope by.", TsType = "string", IsOptional = true },
            new() { Name = "skip", Description = "Pagination skip (default 0).", TsType = "number", IsOptional = true },
            new() { Name = "take", Description = "Pagination take (default 100, max 500).", TsType = "number", IsOptional = true },
        },
    };

    // -- Write tools --

    public static readonly ToolDescriptor CreateWorkItem = new()
    {
        Name = "ild_create_workitem",
        Label = "Create Work Item",
        Description = "Create a new work item in the Backlog column. repositoryId is required — use ild_list_repositories to discover ids. Tags determine which loop template executes the work item.",
        EndpointPath = "api/v1/agent/workitems",
        HttpMethod = HttpMethod.Post,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "title", Description = "Title (1..512 chars).", TsType = "string", IsBodyParam = true },
            new() { Name = "repositoryId", Description = "Required Repository GUID.", TsType = "string", IsBodyParam = true },
            new() { Name = "description", Description = "Description (markdown).", TsType = "string", IsOptional = true, IsBodyParam = true },
            new() { Name = "dependencies", Description = "Work item GUIDs this item depends on.", TsType = "string-array", IsOptional = true, IsBodyParam = true },
            new() { Name = "createdByLoopRunId", Description = "Originating LoopRun GUID. Defaults to current run.", TsType = "string", IsOptional = true, IsBodyParam = true },
            new() { Name = "tags", Description = "Tags matching loop template names.", TsType = "string-array", IsOptional = true, IsBodyParam = true },
        },
    };

    // -- Worktree preview controls (ADR-0011) --
    //
    // Mirror the MCP PreviewTools and the agent-API preview endpoints. Each takes
    // an explicit workItemId (the agent reads it from the Chat Context). Path
    // params are substituted from the matching {placeholder} in EndpointPath.

    public static readonly ToolDescriptor GetPreview = new()
    {
        Name = "ild_get_preview",
        Label = "Get Preview",
        Description = "Get the worktree preview status for a work item: configured state, resolved profile, and each service's state (port, health URL, public URL).",
        EndpointPath = "api/v1/agent/workitems/{workItemId}/preview",
        HttpMethod = HttpMethod.Get,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "workItemId", Description = "Work item GUID (from the Chat Context).", TsType = "string" },
        },
    };

    public static readonly ToolDescriptor StartPreview = new()
    {
        Name = "ild_start_preview",
        Label = "Start Preview",
        Description = "Start the worktree preview for a work item — runs install steps (unless skipInstall) and starts every service.",
        EndpointPath = "api/v1/agent/workitems/{workItemId}/preview/start",
        HttpMethod = HttpMethod.Post,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "workItemId", Description = "Work item GUID (from the Chat Context).", TsType = "string" },
            new() { Name = "profileName", Description = "Optional profile name; defaults to the config default.", TsType = "string", IsOptional = true, IsBodyParam = true },
            new() { Name = "skipInstall", Description = "Skip install steps (default false).", TsType = "boolean", IsOptional = true, IsBodyParam = true },
        },
    };

    public static readonly ToolDescriptor StopPreview = new()
    {
        Name = "ild_stop_preview",
        Label = "Stop Preview",
        Description = "Stop the worktree preview for a work item — tears down all running services.",
        EndpointPath = "api/v1/agent/workitems/{workItemId}/preview/stop",
        HttpMethod = HttpMethod.Post,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "workItemId", Description = "Work item GUID (from the Chat Context).", TsType = "string" },
        },
    };

    public static readonly ToolDescriptor StartPreviewService = new()
    {
        Name = "ild_start_preview_service",
        Label = "Start Preview Service",
        Description = "Start a single preview service by name, leaving the others untouched.",
        EndpointPath = "api/v1/agent/workitems/{workItemId}/preview/services/{service}/start",
        HttpMethod = HttpMethod.Post,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "workItemId", Description = "Work item GUID (from the Chat Context).", TsType = "string" },
            new() { Name = "service", Description = "Service name as declared in ild.config.json.", TsType = "string" },
            new() { Name = "profileName", Description = "Optional profile name; defaults to the config default.", TsType = "string", IsOptional = true, IsBodyParam = true },
            new() { Name = "skipInstall", Description = "Skip install steps (default false).", TsType = "boolean", IsOptional = true, IsBodyParam = true },
        },
    };

    public static readonly ToolDescriptor StopPreviewService = new()
    {
        Name = "ild_stop_preview_service",
        Label = "Stop Preview Service",
        Description = "Stop a single running preview service by name, leaving the others running.",
        EndpointPath = "api/v1/agent/workitems/{workItemId}/preview/services/{service}/stop",
        HttpMethod = HttpMethod.Post,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "workItemId", Description = "Work item GUID (from the Chat Context).", TsType = "string" },
            new() { Name = "service", Description = "Service name as declared in ild.config.json.", TsType = "string" },
        },
    };

    public static readonly ToolDescriptor GetPreviewServiceConfig = new()
    {
        Name = "ild_get_preview_service_config",
        Label = "Get Preview Service Config",
        Description = "Get one service's entry from the worktree's ild.config.json as raw JSON.",
        EndpointPath = "api/v1/agent/workitems/{workItemId}/preview/services/{service}/config",
        HttpMethod = HttpMethod.Get,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "workItemId", Description = "Work item GUID (from the Chat Context).", TsType = "string" },
            new() { Name = "service", Description = "Service name as declared in ild.config.json.", TsType = "string" },
        },
    };

    public static readonly ToolDescriptor UpdatePreviewServiceConfig = new()
    {
        Name = "ild_update_preview_service_config",
        Label = "Update Preview Service Config",
        Description = "Replace one service's entry in the worktree's ild.config.json with the supplied JSON. Validated like preview start; 'name' must match the service. Takes effect on the next start.",
        EndpointPath = "api/v1/agent/workitems/{workItemId}/preview/services/{service}/config",
        HttpMethod = HttpMethod.Put,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "workItemId", Description = "Work item GUID (from the Chat Context).", TsType = "string" },
            new() { Name = "service", Description = "Service name as declared in ild.config.json.", TsType = "string" },
            new() { Name = "config", Description = "The full service object as JSON.", TsType = "string", IsBodyParam = true },
        },
    };

    public static readonly ToolDescriptor GetPreviewLogs = new()
    {
        Name = "ild_get_preview_logs",
        Label = "Get Preview Logs",
        Description = "Read the tail of a preview service's captured stdout/stderr log — useful to see why a service failed to start.",
        EndpointPath = "api/v1/agent/workitems/{workItemId}/preview/logs",
        HttpMethod = HttpMethod.Get,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "workItemId", Description = "Work item GUID (from the Chat Context).", TsType = "string" },
            new() { Name = "service", Description = "Service name as declared in ild.config.json.", TsType = "string" },
        },
    };

    // -- Loop Editor context (ADR-0011) --
    //
    // Mirror the MCP LoopTools and the agent-API current-loop endpoints. Scoped to
    // the chat session via the X-ILD-Chat-Session-Id header the generated client
    // sends. update_current_loop is a full-document replacement applied to the open
    // editor's transient client state — there is no persist tool.

    public static readonly ToolDescriptor GetCurrentLoop = new()
    {
        Name = "ild_get_current_loop",
        Label = "Get Current Loop",
        Description = "Read the loop the user currently has open in the Loop Editor as an ild-loop-template/v1 JSON document (its live, possibly-unsaved nodes and edges). Returns {\"loopEditorOpen\": false} when no loop editor is open.",
        EndpointPath = "api/v1/agent/current-loop",
        HttpMethod = HttpMethod.Get,
        Parameters = Array.Empty<ToolParameterDescriptor>(),
    };

    public static readonly ToolDescriptor UpdateCurrentLoop = new()
    {
        Name = "ild_update_current_loop",
        Label = "Update Current Loop",
        Description = "Replace the loop open in the Loop Editor with a complete ild-loop-template/v1 document (full replacement, NOT a patch). The open editor validates and direct-applies it to the live canvas; on a validation error the loop is left untouched. Edits transient client state only — it never saves and returns no structured ack; re-read with ild_get_current_loop on a later turn to confirm.",
        EndpointPath = "api/v1/agent/current-loop",
        HttpMethod = HttpMethod.Put,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "document", Description = "A complete ild-loop-template/v1 document as JSON.", TsType = "string", IsBodyParam = true },
        },
    };

    // -- Aggregate (must be last to ensure all above are initialized first) --

    public static readonly ToolDescriptor[] All =
    [
        ListWorkItems,
        GetWorkItem,
        CreateWorkItem,
        ListRepositories,
        ListLoopTemplates,
        ListLoopRuns,
        GetPreview,
        StartPreview,
        StopPreview,
        StartPreviewService,
        StopPreviewService,
        GetPreviewServiceConfig,
        UpdatePreviewServiceConfig,
        GetPreviewLogs,
        GetCurrentLoop,
        UpdateCurrentLoop,
    ];
}
