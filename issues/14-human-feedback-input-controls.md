## Parent

PRD.md

## What to build

Build the Human Feedback input UI and retry/cleanup controls. When a WorkItem is in Human Feedback due to a Human node awaiting input, the user can provide text input and choose Continue or Reject. For failed or cancelled WorkItems, the user chooses "Cleanup -> Done" (discard worktree) or "Cleanup -> Backlog" (reset for re-planning).

This UI appears in the WorkItem detail modal when the WorkItem's status is Human Feedback.

## Acceptance criteria

- [ ] WorkItem modal shows Human Feedback input section when status is Human Feedback and reason is "Human Input Needed"
- [ ] Input section has a text area for providing context/messages to the event log
- [ ] "Continue" button resumes the LoopRun, appending input to the event log
- [ ] "Reject" button routes to the `on_failure` edge of the current Human node
- [ ] For failed/cancelled WorkItems: "Cleanup -> Done" button destroys worktree and marks WorkItem Done
- [ ] For failed/cancelled WorkItems: "Cleanup -> Backlog" button resets WorkItem to Backlog, destroys worktree, clears run state
- [ ] API endpoints support: POST human feedback input, POST retry, POST cleanup-to-done, POST cleanup-to-backlog
- [ ] Backend tests cover: Continue resumes run, Reject routes to failure edge, Cleanup-Done destroys worktree, Cleanup-Backlog resets state
- [ ] Frontend tests cover: input section renders, buttons call correct API endpoints, state transitions reflect in UI
- [ ] `vp check` and `vp test` pass

## Blocked by

- Blocked by #13 (Human Feedback badges and notifications must exist first)
