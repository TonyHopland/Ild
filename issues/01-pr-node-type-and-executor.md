## Parent

PRD.md

## Status

**COMPLETE**

## What to build

Add `PR` as a new NodeType alongside Start, Cmd, AI, Human, and Cleanup. Implement PRNodeExecutor that creates a pull request (or reuses an existing one via `WorkItem.PrUrl`), waits for webhook events, and routes merge to `on_success` (toward Cleanup) and rejection to `on_failure` (back to AI coder). Update the frontend type definitions to include the PR node type.

The PR Node makes the PR lifecycle explicit in the loop graph rather than implicit engine behavior.

## Acceptance criteria

- [x] `NodeType` enum includes `PR` value
- [x] `PRNodeExecutor` class exists in `ILD.Core/Services/Implementations/Executors/`
- [x] PRNodeExecutor creates a PR via `IRemoteProvider` or reuses existing PR from `WorkItem.PrUrl`
- [x] PRNodeExecutor waits for webhook events and routes merge to `on_success`, rejection to `on_failure`
- [x] PRNodeExecutor is registered in DI container
- [x] Frontend `NodeType` type in `frontend/src/types/index.ts` includes `PR`
- [x] Backend tests cover: PR creation, PR reuse, merge routing, rejection routing, webhook event handling
- [x] `vp check` and `vp test` pass

## Blocked by

None - can start immediately
