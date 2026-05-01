## What to build

The Taskboard supports drag-and-drop between status columns but cannot be operated with the keyboard, which is an accessibility regression and blocks screen-reader users.

## Acceptance criteria

- [x] Each WorkItem card is focusable and exposes an accessible name
- [x] When focused, arrow keys (or a documented shortcut) move the card to the previous/next column
- [x] Keyboard moves trigger the same `POST /workitems/{id}/transition` as drag/drop
- [x] `aria-live` region announces transitions
- [x] At least one component test exercises the keyboard path

## Blocked by

None. Spun out of #025 part E.
