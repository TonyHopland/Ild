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
            new() { Name = "description", Description = "Description (markdown, up to 4096 chars).", TsType = "string", IsOptional = true, IsBodyParam = true },
            new() { Name = "dependencies", Description = "Work item GUIDs this item depends on.", TsType = "string-array", IsOptional = true, IsBodyParam = true },
            new() { Name = "createdByLoopRunId", Description = "Originating LoopRun GUID. Defaults to current run.", TsType = "string", IsOptional = true, IsBodyParam = true },
            new() { Name = "tags", Description = "Tags matching loop template names.", TsType = "string-array", IsOptional = true, IsBodyParam = true },
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
    ];
}
