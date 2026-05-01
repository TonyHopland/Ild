## What to build

Frontend build configuration is inconsistent and the bundle is shipped as a single chunk.

**A — `@xyflow/react` only declared at the repo root:** [LoopEditor.tsx](frontend/src/pages/LoopEditor.tsx#L5-L13) imports `@xyflow/react`, but it is not in [frontend/package.json](frontend/package.json). Add it as a direct dependency of the frontend package so resolution does not depend on workspace hoisting.

**B — Hardcoded API base URL:** [api.ts](frontend/src/services/api.ts#L3) defines `const API_BASE = "/api/v1"`. Read from `import.meta.env.VITE_API_BASE` with `/api/v1` as the default so deployments can override.

**C — Inline style/color maps in render path:** [WorkItemCard.tsx](frontend/src/components/WorkItemCard.tsx#L9-L28) recreates `REASON_STYLES` and `priorityColors` on every render. Move to module scope (or `useMemo` if they actually depend on props).

**D — Code splitting:** Heavy pages (`LoopEditor` pulling in React Flow) ship in the initial bundle. Convert routes to `React.lazy` + `<Suspense>` so the initial taskboard load is fast.

**E — Two parallel Vite configs:** Both [/workspaces/ild/vite.config.ts](vite.config.ts) and [frontend/vite.config.ts](frontend/vite.config.ts) exist. Confirm only one is used and delete the dead one.

## Acceptance criteria

- [x] `@xyflow/react` listed in `frontend/package.json` dependencies
- [x] API base URL read from `import.meta.env.VITE_API_BASE`
- [x] Static maps in `WorkItemCard` (and similar) live at module scope
- [x] At least `LoopEditor` and `LoopRunMonitor` are lazy-loaded
- [ ] Only one `vite.config.ts` remains; `vp build` succeeds — _kept both: root config drives `vp` (lint/fmt/staged), frontend config drives dev server + build_

## Blocked by

None.
