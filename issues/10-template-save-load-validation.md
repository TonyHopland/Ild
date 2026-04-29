## Parent

PRD.md

## What to build

Implement the save and load round-trip for loop templates through the React Flow editor. Saving a template validates the graph (Start node exists, Cleanup node exists, all nodes reachable from Start, at least one path leads to Cleanup, unknown placeholder validation for AI node prompts). Validation errors display inline on the canvas.

On successful save, the template is auto-versioned. Loading a template restores the full graph state from the API.

## Acceptance criteria

- [ ] Save button calls `POST /api/v1/looptemplates` (create) or `PUT /api/v1/looptemplates/{id}` (update) with the full graph DTO
- [ ] Before saving, client-side validation runs: Start node exists, Cleanup node exists, all nodes reachable, path to Cleanup exists
- [ ] Validation errors display as inline badges on the canvas with descriptions
- [ ] AI node prompt templates are validated for unknown placeholders via `POST /api/v1/looptemplates/validate`
- [ ] On successful save, a success toast/notification is shown
- [ ] Loading a template by ID from the list restores nodes, edges, and all configuration onto the canvas
- [ ] Auto-versioning: each save increments the version (verified via API response)
- [ ] Frontend tests cover: save sends correct DTO, validation errors display, load restores graph state, unknown placeholder error
- [ ] Backend tests (if validation logic changes): cover new validation rules
- [ ] `vp check` and `vp test` pass

## Blocked by

- Blocked by #09 (Edge drawing must exist so the full graph can be saved)
