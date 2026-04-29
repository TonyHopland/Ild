## Parent

PRD.md

## What to build

Implement per-node tool allowlists for AI nodes and ILD API tools (`ild_create_workitem`, `ild_read_workitem`, `ild_list_loop_templates`). AI node tool access is tiered: read operations (file read, git read) are always available; write operations (file write, git commit/push, command execution) are opt-in per node via the node configuration.

The LoopEditor's AI node config panel includes checkboxes for enabling write tools. The `AINodeExecutor` enforces the allowlist at execution time. ILD API tools allow AI to create and read WorkItems and list LoopTemplates for AI-driven work item decomposition.

## Acceptance criteria

- [ ] `LoopNode` config JSON stores a `toolAllowlist` array field
- [ ] AI node config panel in LoopEditor shows tool checkboxes: FileRead (always on), FileWrite, GitCommit, GitPush, CommandExecute, ILDCreateWorkItem, ILDReadWorkItem, ILDListLoopTemplates
- [ ] `AINodeExecutor` reads the allowlist and only exposes permitted tools to the LLM
- [ ] `ild_create_workitem` tool: creates a WorkItem via `IWorkItemManager`
- [ ] `ild_read_workitem` tool: reads a WorkItem by ID via `IWorkItemManager`
- [ ] `ild_list_loop_templates` tool: lists LoopTemplates via `ILoopTemplateManager`
- [ ] Default tool access is read-only (FileRead only) when allowlist is empty or not set
- [ ] Backend tests cover: allowlist enforcement (write tools blocked when not allowed), ILD API tool execution, read-only default
- [ ] Frontend tests cover: tool checkboxes render, config serializes to correct JSON
- [ ] `vp check` and `vp test` pass

## Blocked by

- Blocked by #10 (Template save/load must work for tool allowlist config to persist)
