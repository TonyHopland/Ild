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
            new() { Name = "branchNameOverride", Description = "Custom branch name used verbatim by every run of the item, instead of the generated per-run name. Must be a valid git branch name.", TsType = "string", IsOptional = true, IsBodyParam = true },
            new() { Name = "baseBranchOverride", Description = "Branch every run of the item starts from and opens its PR against, instead of the repository's default branch. Must exist on the remote at run start.", TsType = "string", IsOptional = true, IsBodyParam = true },
        },
    };

    // -- Branch sync (ADR-0014) --
    //
    // Mirrors the MCP BranchTools and the agent-API pull-branch endpoint. The
    // agent uid cannot authenticate to the remote itself, so it asks the
    // orchestrator to fetch and rebase on its behalf.

    public static readonly ToolDescriptor PullBranch = new()
    {
        Name = "ild_pull_branch",
        Label = "Pull Branch",
        Description = "Pull the latest changes from origin into this work item's run branch — fetches with ILD's repository credentials and rebases the branch onto origin/<branch>. Use it to pick up commits pushed after the run started; git in the worktree has no credentials. The fetch brings down every branch on the remote (all of origin/*, stale ones pruned), so afterwards you can read any of them locally with plain git — origin/<default branch> included, which is how you check whether your branch needs it merged in. It does NOT merge or rebase the default branch into yours. Returns an outcome of Updated, AlreadyUpToDate, NoRemoteBranch, DirtyWorktree (commit first; 'files' lists them), Conflict (rebase aborted, branch untouched; 'files' lists the conflicts to resolve) or RebaseRefused (git would not rebase at all — nothing to resolve, read 'message').",
        EndpointPath = "api/v1/agent/workitems/{workItemId}/pull-branch",
        HttpMethod = HttpMethod.Post,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "workItemId", Description = "Work item GUID (from the Chat Context).", TsType = "string" },
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

    public static readonly ToolDescriptor GetCiLog = new()
    {
        Name = "ild_get_ci_log",
        Label = "Get CI Log",
        Description = "Read the tail of a failing CI check's log for a work item's pull request. Use when a CI failure reason names a check and its summary is not enough to fix it — the check id comes from that reason. The log is fetched server-side with the forge credentials. Returns {available, text, lines, offset, totalLines, truncated, message}: the newest lines come first to hand, so page backwards by raising offset when the error is above the window, and treat truncated=true as 'there is more, ask again' rather than 'that was all'. available=false with a message means the provider has no log to fetch (its CI lives outside the forge) — read the message and use the URL in it.",
        EndpointPath = "api/v1/agent/workitems/{workItemId}/ci-log",
        HttpMethod = HttpMethod.Get,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "workItemId", Description = "Work item GUID (from the Chat Context, or the CI failure reason).", TsType = "string" },
            new() { Name = "checkId", Description = "Id of the failing check, as named in the CI failure reason.", TsType = "string" },
            new() { Name = "tailLines", Description = "How many lines to return, counting back from the end (default 200, max 2000).", TsType = "number", IsOptional = true },
            new() { Name = "offset", Description = "Lines to skip from the end before taking the window — raise it to walk backwards through the log (default 0).", TsType = "number", IsOptional = true },
        },
    };

    // -- Loop Editor context (ADR-0011) --
    //
    // Mirror the MCP LoopTools and the agent-API current-loop endpoints. Scoped to
    // the chat session via the X-ILD-Chat-Session-Id header the generated client
    // sends. update_current_loop is a full-document replacement applied to the open
    // editor's transient client state — there is no persist tool.

    public static readonly ToolDescriptor GetLoopAuthoringGuide = new()
    {
        Name = "ild_get_loop_authoring_guide",
        Label = "Get Loop Authoring Guide",
        Description = "Read the loop authoring guide: the node/edge vocabulary, the config field semantics you cannot infer from the name, the graph rules a human's save enforces, and the authoring practices. Call this before writing or restructuring a loop, and again whenever you are unsure of a rule — the chat sends it once per session, so this is how you get it back.",
        EndpointPath = "api/v1/agent/loop-authoring-guide",
        HttpMethod = HttpMethod.Get,
        Parameters = Array.Empty<ToolParameterDescriptor>(),
    };

    public static readonly ToolDescriptor GetCurrentLoop = new()
    {
        Name = "ild_get_current_loop",
        Label = "Get Current Loop",
        Description = "Read the loop the user currently has open in the Loop Editor as an ild-loop-template/v1 JSON document (its live, possibly-unsaved nodes and edges). Returns {\"loopEditorOpen\": false} when no loop editor is open.",
        EndpointPath = "api/v1/agent/current-loop",
        HttpMethod = HttpMethod.Get,
        Parameters = Array.Empty<ToolParameterDescriptor>(),
    };

    public static readonly ToolDescriptor GetLoopNode = new()
    {
        Name = "ild_get_loop_node",
        Label = "Get Loop Node",
        Description = "Read a single node from the loop open in the Loop Editor by its id. Returns { id, type, label, config } as JSON with the config's prompt/text fields decoded (plain text, not escaped) — read this first, then craft a unique old_string for ild_edit_loop_node_field. Returns {\"loopEditorOpen\": false} when no loop editor is open, or a 404 when no node has that id.",
        EndpointPath = "api/v1/agent/current-loop/nodes/{nodeId}",
        HttpMethod = HttpMethod.Get,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "nodeId", Description = "The id of the node to read (from ild_get_current_loop).", TsType = "string" },
        },
    };

    public static readonly ToolDescriptor EditLoopNodeField = new()
    {
        Name = "ild_edit_loop_node_field",
        Label = "Edit Loop Node Field",
        Description = "Targeted find-and-replace on ONE node config field's decoded text (the primary way to tweak a prompt). old_string is plain text — you never handle JSON escaping, the server re-encodes. old_string must match EXACTLY ONCE: zero or multiple matches change nothing and report the count. Returns { applied, matchCount, validationErrors }; a resulting invalid graph is rejected and the canvas is left untouched. Transient client state only.",
        EndpointPath = "api/v1/agent/current-loop/nodes/{nodeId}/edit-field",
        HttpMethod = HttpMethod.Post,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "nodeId", Description = "The id of the node to edit.", TsType = "string" },
            new() { Name = "field", Description = "The config field to edit, e.g. 'prompt', 'command', 'prDescriptionTemplate', 'output'.", TsType = "string", IsBodyParam = true },
            new() { Name = "oldString", Description = "The decoded text to find. Must occur exactly once in the field.", TsType = "string", IsBodyParam = true },
            new() { Name = "newString", Description = "The decoded replacement text.", TsType = "string", IsBodyParam = true },
        },
    };

    public static readonly ToolDescriptor SetLoopNodeField = new()
    {
        Name = "ild_set_loop_node_field",
        Label = "Set Loop Node Field",
        Description = "Overwrite ONE node config field wholesale with a new value (the intentional replace-all path; use instead of ild_edit_loop_node_field to replace the whole field). The field is created if absent. Returns { applied, matchCount, validationErrors }; a resulting invalid graph is rejected and the canvas is left untouched. Transient client state only.",
        EndpointPath = "api/v1/agent/current-loop/nodes/{nodeId}/set-field",
        HttpMethod = HttpMethod.Post,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "nodeId", Description = "The id of the node to edit.", TsType = "string" },
            new() { Name = "field", Description = "The config field to overwrite, e.g. 'prompt', 'command'.", TsType = "string", IsBodyParam = true },
            new() { Name = "value", Description = "The new value stored as the field's text.", TsType = "string", IsBodyParam = true },
        },
    };

    public static readonly ToolDescriptor GetLoopFile = new()
    {
        Name = "ild_get_loop_file",
        Label = "Get Loop File",
        Description = "Read the whole loop open in the Loop Editor as raw ild-loop-template/v1 JSON text — the exact bytes to target with ild_edit_loop_file. Returns {\"loopEditorOpen\": false} when no loop editor is open. For a single node prefer ild_get_loop_node (decoded, cheaper).",
        EndpointPath = "api/v1/agent/current-loop/file",
        HttpMethod = HttpMethod.Get,
        Parameters = Array.Empty<ToolParameterDescriptor>(),
    };

    public static readonly ToolDescriptor EditLoopFile = new()
    {
        Name = "ild_edit_loop_file",
        Label = "Edit Loop File",
        Description = "Targeted find-and-replace on the RAW JSON of the whole loop document — the escape hatch for structural nudges (edges, ids, adding a node) a field edit can't reach. You are editing raw JSON, so old_string must include correct JSON escaping (call ild_get_loop_file first). old_string must match EXACTLY ONCE. Returns { applied, matchCount, validationErrors }; an edit that produces invalid JSON or an invalid graph is rejected and the canvas is left untouched. Prefer ild_edit_loop_node_field for prompt text.",
        EndpointPath = "api/v1/agent/current-loop/file/edit",
        HttpMethod = HttpMethod.Post,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "oldString", Description = "The raw-JSON text to find. Must occur exactly once in the document.", TsType = "string", IsBodyParam = true },
            new() { Name = "newString", Description = "The raw-JSON replacement text.", TsType = "string", IsBodyParam = true },
        },
    };

    public static readonly ToolDescriptor UpdateCurrentLoop = new()
    {
        Name = "ild_update_current_loop",
        Label = "Update Current Loop",
        Description = "Replace the loop open in the Loop Editor with a complete ild-loop-template/v1 document (full replacement, NOT a patch). ESCAPE HATCH: prefer the targeted edits (ild_edit_loop_node_field / ild_edit_loop_file), which cannot corrupt unrelated nodes. The server validates the document and returns a synchronous ack { applied, matchCount, validationErrors }; on a validation error the edit is rejected and the loop is left untouched. On success the live canvas updates immediately. Transient client state only — it never saves.",
        EndpointPath = "api/v1/agent/current-loop",
        HttpMethod = HttpMethod.Put,
        Parameters = new ToolParameterDescriptor[]
        {
            new() { Name = "document", Description = "A complete ild-loop-template/v1 document as JSON.", TsType = "string", IsBodyParam = true },
        },
    };

    // -- Loop variables (ADR-0011) --
    //
    // Mirror the MCP VariableTools and the agent-API variable endpoints.
    // Scoped to the loop run via the X-ILD-Run-Id header the generated client sends.

    public static readonly ToolDescriptor GetLoopVariables = new()
    {
        Name = "ild_get_loop_variables",
        Label = "Get Loop Variables",
        Description =
            "List all loop variables set on the current loop run. Returns an array of {name, value, updatedAt}. Use this to read hand-off values written by an earlier node.",
        EndpointPath = "api/v1/agent/variables",
        HttpMethod = HttpMethod.Get,
        Parameters = Array.Empty<ToolParameterDescriptor>(),
    };

    public static readonly ToolDescriptor SetLoopVariable = new()
    {
        Name = "ild_set_loop_variable",
        Label = "Set Loop Variable",
        Description =
            "Create or overwrite a loop variable on the current loop run. The name must start with a letter and contain only letters, digits, and underscores. Use this to hand off text to a later AI or to the PR node.",
        EndpointPath = "api/v1/agent/variables/{name}",
        HttpMethod = HttpMethod.Put,
        Parameters = new ToolParameterDescriptor[]
        {
            new()
            {
                Name = "name", Description =
                    "Variable name: starts with a letter, then letters/digits/underscores (e.g. handoff, pr_summary).",
                TsType = "string",
            },
            new()
            {
                Name = "value", Description = "Value to store (up to 8192 chars).", TsType = "string", IsBodyParam = true,
            },
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
        PullBranch,
        GetPreview,
        StartPreview,
        StopPreview,
        StartPreviewService,
        StopPreviewService,
        GetPreviewServiceConfig,
        UpdatePreviewServiceConfig,
        GetPreviewLogs,
        GetCiLog,
        GetLoopAuthoringGuide,
        GetCurrentLoop,
        GetLoopNode,
        EditLoopNodeField,
        SetLoopNodeField,
        GetLoopFile,
        EditLoopFile,
        UpdateCurrentLoop,
        GetLoopVariables,
        SetLoopVariable,
    ];
}
