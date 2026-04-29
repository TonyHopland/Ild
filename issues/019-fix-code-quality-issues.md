## What to build

The frontend has significant code quality issues that affect maintainability and correctness:

**A — Inline `<style>` tags in React components:** LoopEditor has ~898 lines of inline CSS. Styles are re-injected on every render and duplicated across the document. Move to CSS modules or the existing CSS files.

**B — Silent error swallowing:** `WorkItemModal` catches API errors with empty `catch(() => {})` blocks. Show error feedback to the user.

**C — No loading states on form submissions:** `handleSubmit` in WorkItemModal has no loading/disabled state. Users can double-click and cause duplicate API requests.

**D — `AiProvider` create has no `ModelState` validation:** `AiProvidersController.Create` doesn't check `ModelState.IsValid`.

**E — Human feedback resume path:** `HumanNodeExecutor` is dead code (engine short-circuits Human nodes directly). Remove it. Currently when human feedback is submitted, the engine resumes but has no resume path for Human nodes (only PR nodes), causing an infinite loop back to `WaitingHuman`. Fix: `SubmitHumanFeedbackInputAsync` sets the Human `LoopRunNode` to `Succeeded` with the input as `Output` (routes to `on_success` edge). `RejectHumanFeedbackAsync` already sets it to `Failed` (routes to `on_failure` edge). Engine's resume path (lines 143-186) needs a Human node handler alongside the existing PR handler. The human input also becomes `{{PreviousNode.Output}}` for downstream nodes per CONTEXT.md.

**F — `PRNodeExecutor` missing null check on `prResult`:** If `CreatePullRequestAsync` returns null, a `NullReferenceException` is thrown.

## Acceptance criteria

- [ ] Inline styles extracted to CSS module files (at minimum for LoopEditor)
- [ ] API errors in WorkItemModal display a user-visible error message
- [ ] Form submit buttons are disabled during pending requests
- [ ] `AiProvidersController.Create` validates `ModelState` before creating entity
- [ ] `HumanNodeExecutor` removed (dead code)
- [ ] `SubmitHumanFeedbackInputAsync` sets Human LoopRunNode to Succeeded with input as Output
- [ ] `RejectHumanFeedbackAsync` sets Human LoopRunNode to Failed (already done)
- [ ] Engine resume path handles Human node status (Succeeded → on_success, Failed → on_failure)
- [ ] Human node input available as `{{PreviousNode.Output}}` for downstream nodes
- [ ] `PRNodeExecutor` handles null `prResult` gracefully
- [ ] `GetRunStatusAsync` returns an optional or throws for non-existent runs (not ambiguous `Failed`)

## Blocked by

- Blocked by #5 (Fix Store Update methods — the resume path relies on correct store behavior)
