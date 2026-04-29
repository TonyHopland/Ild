## Parent

PRD.md

## Status

**COMPLETE**

## What to build

Add a `DefaultIntakeStatus` field to the Repository model that controls whether new WorkItems land in Backlog or Work Queue. When a WorkItem is created, check the associated repository's gating setting to determine the initial status. Update the API to expose and accept this setting.

Per CONTEXT.md: "Backlog vs Work Queue landing is per-Repository, not global or per-WorkItem."

## Acceptance criteria

- [x] `Repository` model has `DefaultIntakeStatus` property (enum: Backlog or WorkQueue)
- [x] Database migration adds the column with a default value
- [x] `WorkItemManager.CreateWorkItem` sets initial status based on the repository's `DefaultIntakeStatus`
- [x] Repository API endpoints (`POST /api/v1/repositories`, `PUT`) accept and return the field
- [x] `RepositoryDto` includes the new field
- [x] Backend tests cover: WorkItem lands in Backlog when repo setting is Backlog, WorkItem lands in WorkQueue when repo setting is WorkQueue
- [x] `vp check` and `vp test` pass

## Blocked by

None - can start immediately
