## What to build

The `loopRunService` in `frontend/src/services/auth.ts` has three broken API paths:

- `getAll()` hits `/looptemplates` instead of `/loopruns`
- `getById()` uses singular `/looprun/${id}` instead of `/loopruns/${id}`
- `cancel()` uses singular `/looprun/${id}/cancel` instead of `/loopruns/${id}/cancel`

The LoopRunMonitor page receives loop templates instead of loop runs, and cancel/get-by-id 404.

## Acceptance criteria

- [ ] `loopRunService.getAll()` calls `GET /loopruns`
- [ ] `loopRunService.getById(id)` calls `GET /loopruns/${id}`
- [ ] `loopRunService.cancel(id)` calls `POST /loopruns/${id}/cancel`
- [ ] Verify LoopRunMonitor displays actual loop run data
- [ ] Rename service file from `auth.ts` to a proper barrel or split services into their own files

## Blocked by

None - can start immediately
