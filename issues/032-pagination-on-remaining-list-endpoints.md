## What to build

#23 added `skip`/`take` pagination to `WorkItemsController.GetAll`, but other list endpoints are still unbounded:

- `LoopTemplatesController.GetAll`
- `LoopRunsController.GetAll`
- `AiProvidersController.GetAll`
- `RepositoriesController.GetAll`
- `RemoteProvidersController.GetAll`
- `WorkItemsController.GetRuns`, `GetDependencies`

Apply the same `skip`/`take` (default 100, max 500) convention server-side and propagate the params through the frontend service layer.

## Acceptance criteria

- [x] All list endpoints above accept `skip` and `take` query params with sensible defaults and a server-side cap
- [x] Frontend services (e.g. `loopTemplateService.getAll`, `loopRunService.getAll`) accept optional pagination args
- [x] Existing tests still pass; add at least one test asserting the cap is enforced

## Blocked by

None. Spun out of #023 part A.
