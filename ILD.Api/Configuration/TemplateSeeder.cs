using ILD.Data.DTOs;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;

namespace ILD.Api.Configuration;

/// <summary>
/// Creates the three PRD seed loop templates on first run.
/// </summary>
public static class TemplateSeeder
{
    public static async Task SeedAsync(ILoopTemplateStore templateStore, ILoopTemplateManager mgr)
    {
        var existing = await templateStore.GetAllAsync();
        if (existing.Any()) return;

        await CreateSimpleCodeChangeAsync(mgr);
        await CreateAiAssistedFeatureAsync(mgr);
        await CreatePlanAsync(mgr);
    }

    private static async Task CreateSimpleCodeChangeAsync(ILoopTemplateManager mgr)
    {
        var nodes = new List<LoopNodeDto>
        {
            new() { Id = "start", Label = "Start", NodeType = "Start" },
            new() { Id = "build", Label = "Build", NodeType = "Cmd",
                Config = new Dictionary<string, object> { ["command"] = "echo build" } },
            new() { Id = "test", Label = "Test", NodeType = "Cmd",
                Config = new Dictionary<string, object> { ["command"] = "echo test" } },
            new() { Id = "cleanup", Label = "Cleanup", NodeType = "Cleanup" },
        };
        var edges = new List<LoopNodeEdgeDto>
        {
            new() { SourceNodeId = "start", TargetNodeId = "build", EdgeType = "OnSuccess" },
            new() { SourceNodeId = "build", TargetNodeId = "test", EdgeType = "OnSuccess" },
            new() { SourceNodeId = "test", TargetNodeId = "cleanup", EdgeType = "OnSuccess" },
        };
        await mgr.CreateLoopTemplateAsync(
            "Simple Code Change",
            "Build then test then cleanup",
            new LoopTemplateGraph(Guid.Empty, nodes, edges));
    }

    private static async Task CreateAiAssistedFeatureAsync(ILoopTemplateManager mgr)
    {
        var nodes = new List<LoopNodeDto>
        {
            new() { Id = "start", Label = "Start", NodeType = "Start" },
            new() { Id = "ai", Label = "AI Implement", NodeType = "AI",
                Config = new Dictionary<string, object>
                {
                    ["prompt"] = "Implement: {{WorkItem.Description}}",
                    ["model"] = "default",
                } },
            new() { Id = "build", Label = "Build", NodeType = "Cmd",
                Config = new Dictionary<string, object> { ["command"] = "echo build" } },
            new() { Id = "review", Label = "Human Review", NodeType = "Human" },
            new() { Id = "cleanup", Label = "Cleanup", NodeType = "Cleanup" },
        };
        var edges = new List<LoopNodeEdgeDto>
        {
            new() { SourceNodeId = "start", TargetNodeId = "ai", EdgeType = "OnSuccess" },
            new() { SourceNodeId = "ai", TargetNodeId = "build", EdgeType = "OnSuccess" },
            new() { SourceNodeId = "build", TargetNodeId = "review", EdgeType = "OnSuccess" },
            new() { SourceNodeId = "build", TargetNodeId = "ai", EdgeType = "OnFailure" },
            new() { SourceNodeId = "review", TargetNodeId = "cleanup", EdgeType = "OnSuccess" },
        };
        await mgr.CreateLoopTemplateAsync(
            "AI-Assisted Feature",
            "AI implements, then human reviews",
            new LoopTemplateGraph(Guid.Empty, nodes, edges));
    }

    private static async Task CreatePlanAsync(ILoopTemplateManager mgr)
    {
        var nodes = new List<LoopNodeDto>
        {
            new() { Id = "start", Label = "Start", NodeType = "Start" },
            new() { Id = "ai-grill", Label = "AI Grill", NodeType = "AI",
                Config = new Dictionary<string, object>
                {
                    ["initialPrompt"] = "Create a plan for: {{WorkItem.Title}}\n{{WorkItem.Description}}\n\n\nInterview me relentlessly about every aspect of this plan until we reach a shared understanding. Walk down each branch of the design tree, resolving dependencies between decisions one-by-one. For each question, provide your recommended answer.\n\nAsk the questions one at a time.\n\nIf a question can be answered by exploring the codebase, explore the codebase instead.\n",
                    ["loopPrompt"] = "{{PreviousNode.Output}}",
                    ["sessionInput"] = "incoming",
                    ["sessionOutput"] = "current",
                } },
            new() { Id = "human-plan", Label = "Human Plan", NodeType = "Human",
                Config = new Dictionary<string, object>
                {
                    ["prompt"] = "{{PreviousNode.Output}}",
                } },
            new() { Id = "ai-create-tasks", Label = "AI create tasks", NodeType = "AI",
                Config = new Dictionary<string, object>
                {
                    ["initialPrompt"] = "# To Issues\n\nBreak a plan into independently-grabbable issues using vertical slices (tracer bullets).\n\n## Process\n\n### 1. Gather context\n\nWork from whatever is already in the conversation context.\n\n### 2. Explore the codebase (optional)\n\nIf you have not already explored the codebase, do so to understand the current state of the code. Issue titles and descriptions should use the project's `CONTEXT.md` vocabulary.\n\n### 3. Draft vertical slices\n\nBreak the plan into **tracer bullet** issues. Each issue is a thin vertical slice that cuts through ALL integration layers end-to-end, NOT a horizontal slice of one layer.\n\nSlices may be 'HITL' or 'AFK'. HITL slices require human interaction, such as an architectural decision or a design review. AFK slices can be implemented and merged without human interaction. Prefer AFK over HITL where possible.\n\n<vertical-slice-rules>\n- Each slice delivers a narrow but COMPLETE path through every layer (schema, API, UI, tests)\n- A completed slice is demoable or verifiable on its own\n- Prefer many thin slices over few thick ones\n</vertical-slice-rules>\n\n### 4. Quiz the user\n\nPresent the proposed breakdown as a numbered list. For each slice, show:\n\n- **Title**: short descriptive name\n- **Type**: HITL / AFK\n- **Blocked by**: which other slices (if any) must complete first\n- **User stories covered**: which user stories this addresses (if the source material has them)\n\nAsk the user:\n\n- Does the granularity feel right? (too coarse / too fine)\n- Are the dependency relationships correct?\n- Should any slices be merged or split further?\n- Are the correct slices marked as HITL and AFK?\n\nIterate until the user approves the breakdown.\n\n### 5. Create the issues\n\nFor each approved slice, create a issue using the projects way of defining issues. Use the issue body template below.\n\nCreate issues in dependency order (blockers first) so you can reference real issue numbers in the \"Blocked by\" field.\n\n<issue-template>\n\n## What to build\n\nA concise description of this vertical slice. Describe the end-to-end behavior, not layer-by-layer implementation.\n\n## Acceptance criteria\n\n- [ ] Criterion 1\n- [ ] Criterion 2\n- [ ] Criterion 3\n\n## Blocked by\n\n- Blocked by #<issue-number> (if any)\n\nOr \"None - can start immediately\" if no blockers.\n\n</issue-template>\n\nDo NOT close or modify any parent issue.\n\n\nUse the MCP server to add the issues",
                    ["loopPrompt"] = "{{PreviousNode.Output}}",
                    ["sessionInput"] = "incoming",
                    ["sessionOutput"] = "current",
                } },
            new() { Id = "human-review", Label = "Human review", NodeType = "Human",
                Config = new Dictionary<string, object>
                {
                    ["prompt"] = "{{PreviousNode.Output}}",
                } },
            new() { Id = "cleanup", Label = "Cleanup", NodeType = "Cleanup" },
        };
        var edges = new List<LoopNodeEdgeDto>
        {
            new() { SourceNodeId = "start", TargetNodeId = "ai-grill", EdgeType = "OnSuccess" },
            new() { SourceNodeId = "ai-grill", TargetNodeId = "human-plan", EdgeType = "OnSuccess" },
            new() { SourceNodeId = "human-plan", TargetNodeId = "ai-create-tasks", EdgeType = "OnSuccess" },
            new() { SourceNodeId = "human-plan", TargetNodeId = "ai-grill", EdgeType = "OnRespond" },
            new() { SourceNodeId = "human-plan", TargetNodeId = "cleanup", EdgeType = "OnFailure" },
            new() { SourceNodeId = "ai-create-tasks", TargetNodeId = "human-review", EdgeType = "OnSuccess" },
            new() { SourceNodeId = "human-review", TargetNodeId = "ai-create-tasks", EdgeType = "OnRespond" },
            new() { SourceNodeId = "human-review", TargetNodeId = "cleanup", EdgeType = "OnSuccess" },
            new() { SourceNodeId = "human-review", TargetNodeId = "cleanup", EdgeType = "OnFailure" },
        };
        await mgr.CreateLoopTemplateAsync(
            "Plan",
            "AI proposes plan, human reviews",
            new LoopTemplateGraph(Guid.Empty, nodes, edges));
    }
}
