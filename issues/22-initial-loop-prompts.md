## Parent

PRD.md

## Status

**PENDING**

## What to build

Add `initialPrompt` and `loopPrompt` fields to the AI node config. On first execution of an AI node within a `LoopRun`, the `initialPrompt` is rendered. On subsequent executions (when loopback edges return to the node), the `loopPrompt` is rendered.

The engine tracks `ExecutionCount` per node per run in `LoopRunNode` state. The count is passed to the adapter via `AgentExecutionContext.ExecutionCount`. The adapter uses the count to select which prompt to render (count == 1 → initial, count > 1 → loop).

This supports the roast-me pattern: first run sets context with `{{WorkItem.Description}}`, subsequent runs continue with `{{EventLog.LastN}}` and `{{PreviousNode.Output}}` without duplicating initial context.

## Acceptance criteria

- [ ] `LoopNode` config JSON for AI nodes stores `initialPrompt` and `loopPrompt` string fields
- [ ] `LoopRunNode` or equivalent state tracks `ExecutionCount` per node per run
- [ ] `AgentExecutionContext.ExecutionCount` is populated by `AINodeExecutor` before calling adapter
- [ ] Adapter uses `ExecutionCount == 1` to render `initialPrompt`, `ExecutionCount > 1` to render `loopPrompt`
- [ ] If `loopPrompt` is not set, falls back to `initialPrompt` for all executions
- [ ] If `initialPrompt` is not set, falls back to `loopPrompt` (backward compat with existing `prompt` field)
- [ ] Prompt template validation in #10 covers both prompt fields
- [ ] Backend tests cover: first execution uses initialPrompt, second execution uses loopPrompt, fallback when loopPrompt missing, backward compat with single prompt field
- [ ] `vp check` and `vp test` pass

## Blocked by

- Blocked by #21 (AINodeExecutor must delegate to adapter)
