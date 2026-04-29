## Parent

PRD.md

## Status

**COMPLETE**

## What to build

Enhance the WorkItemModal to support starting a loop run, displaying dependency information with clickable links, and showing loop run history. The create form should include repository and loop template selectors.

When the user clicks Start in the modal, the WorkItem transitions to Running and the LoopEngine begins execution. Dependency links navigate to the dependent WorkItem's modal. Run history lists past LoopRuns with status and timing.

## Acceptance criteria

- [x] WorkItem create form includes repository dropdown and loop template dropdown
- [x] WorkItem detail modal shows a Start button when status is Ready
- [x] Clicking Start calls `POST /api/v1/workitems/{id}/start`, transitions WorkItem to Running, and starts the LoopRun
- [x] Dependency list displays with clickable IDs that open the dependent WorkItem's modal
- [x] Loop run history section lists past LoopRuns with status badge, start time, and duration
- [x] Clicking a run in history navigates to the LoopRun monitor or event log viewer
- [x] Frontend tests cover: modal rendering with start button, dependency links, run history list, form with selectors
- [x] `vp check` and `vp test` pass

## Blocked by

None - can start immediately (backend API endpoints already exist)
