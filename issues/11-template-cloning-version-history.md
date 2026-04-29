## Parent

PRD.md

## Status

**COMPLETE**

## What to build

Add template cloning and version history UI to the LoopEditor. Cloning creates a copy of a LoopTemplate with a new name, starting at version 1. Version history lists all versions of a template with timestamp and node/edge count.

From the template list, a "Clone" button copies the selected template. A "Version History" button shows past versions with their details.

## Acceptance criteria

- [x] Template list item has a "Clone" button
- [x] Cloning calls `POST /api/v1/looptemplates/{id}/clone` with a new name prompt
- [x] Cloned template appears in the list with version 1
- [x] "Version History" button opens a modal/list showing all `LoopTemplateVersion` entries
- [x] Version history shows: version number, created at timestamp, node count, edge count
- [x] Selecting a version from history loads its graph in read-only mode for inspection
- [x] Frontend tests cover: clone button triggers API call, version history list renders, read-only mode prevents editing
- [x] `vp check` and `vp test` pass

## Blocked by

- Blocked by #10 (Save/Load round-trip must work before cloning and version inspection)
