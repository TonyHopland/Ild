## Parent

PRD.md

## Status

**COMPLETE**

## What to build

Build a frontend Settings page for managing repositories. The page lists all repositories, allows creating new repositories with name, clone URL, remote provider selection, worktrees path, and the gating setting (Backlog vs Work Queue intake). Deleting a repository is supported. Wire up to the existing `/api/v1/repositories` endpoints.

Verify that creating a repository with Backlog gating causes new WorkItems to land in Backlog, and WorkQueue gating causes them to land in Work Queue.

## Acceptance criteria

- [x] New route `/repositories`
- [x] Repository list displays: name, clone URL, remote provider, gating setting
- [x] Create form fields: name, clone URL, remote provider dropdown, worktrees path, gating setting selector
- [x] Delete action with confirmation
- [x] Form validation for required fields
- [x] List refresh after create/delete
- [x] Frontend tests cover: form rendering, validation, API call on submit, list rendering
- [ ] End-to-end: create a repository with Backlog gating, create a WorkItem, verify it lands in Backlog
- [x] `vp check` and `vp test` pass

## Blocked by

- None - #02 is complete, backend gating setting is in place
