## What to build

The Human node lifecycle is split between the engine (which short-circuits on `WaitingHuman`) and `WorkItemManager.SubmitHumanFeedbackInputAsync` / `RejectHumanFeedbackAsync`. Today:

- `HumanNodeExecutor` exists but is dead code — the engine never dispatches to it because Human nodes are handled inline.
- `SubmitHumanFeedbackInputAsync` stores the feedback on the WorkItem but does not finalise the Human `LoopRunNode` to `Succeeded` with the input as `Output`.
- The engine resume path does not pick the right outgoing edge based on Human node status (`Succeeded` → `on_success`, `Failed` → `on_failure`).
- Downstream nodes cannot read the human's input via `{{PreviousNode.Output}}` because the LoopRunNode `Output` field is never set.

## Acceptance criteria

- [x] Delete `HumanNodeExecutor` and remove its DI registration
- [x] `SubmitHumanFeedbackInputAsync` finalises the latest Human `LoopRunNode` for the run with `Status = Succeeded`, `Output = input`, `CompletedAt = now`
- [x] `RejectHumanFeedbackAsync` continues to finalise with `Status = Failed`
- [x] Engine resume path selects `on_success` edge when the Human run-node is `Succeeded`, `on_failure` when `Failed`
- [x] `{{PreviousNode.Output}}` rendering picks up the human input on the next AI / Cmd node
- [x] New `LoopEngineTests` covering both branches

## Blocked by

None. Spun out of #019 parts E.
