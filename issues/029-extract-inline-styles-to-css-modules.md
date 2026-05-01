## What to build

Several pages embed inline `<style>` blocks and inline `style={{...}}` props. Extract them to CSS module files for cleaner separation and to enable static optimisation.

Highest-impact files:

- `frontend/src/pages/LoopEditor.tsx`
- `frontend/src/pages/LoopRunMonitor.tsx`
- `frontend/src/components/WorkItemModal.tsx` (large embedded `<style>` block)
- `frontend/src/components/WorkItemCard.tsx`

## Acceptance criteria

- [x] At least `LoopEditor` and `WorkItemModal` move their inline styles to `*.module.css` files
- [x] No regression in visual appearance (manual smoke test)
- [x] Existing component/page tests still pass

## Blocked by

None. Spun out of #019 part A.
