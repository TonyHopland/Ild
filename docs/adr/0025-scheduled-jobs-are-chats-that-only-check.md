# ADR-0025: Scheduled jobs are chats that only check

Some checks should run on a timer and file work items when they find something: a weekly retro of how the loops did, backlog grooming, upstream releases of the agent CLIs ILD wraps, outdated dependencies, flaky tests, docs drift. A **Chat Schedule** runs such a check as an ordinary chat turn of its owner's, started on a cron with a predefined prompt. **A scheduled job only checks; it never changes code.** What needs changing becomes a work item, and the change goes through the normal loop with its own run, branch and PR.

A chat already enforces that without anything new: it has no branch, cannot start runs ([ADR-0011](./0011-context-aware-chat.md)), its PR tools only queue writes that a run's PR node posts, and the items it creates always land in Backlog. It may edit or delete only items its own chat session created ([ADR-0022](./0022-agent-edits-are-human-approved-proposals.md)), so a schedule that starts a new chat per firing can only propose edits to what an earlier firing filed, while one that continues its chat can edit them. A chat also already runs turns on the server with no browser connected, on the same adapters, agent isolation and egress proxy as loop runs.

## Considered options

- **A scheduled loop run.** Rejected: a run exists to change code on a branch, which is exactly what a check must not do, and it needs a work item to run under.
- **A separate job runner with its own tools.** Rejected: it would duplicate the chat's turn execution, isolation and ILD tools, and drift from them.

## Consequences

- The global scheduler pause stops schedules too: a firing due while paused is recorded as skipped, and the schedule fires once when unpaused. Missed firings, after a pause or downtime, merge into one. Run now is a human action and works while paused.
- A firing never interrupts or waits on a busy chat; it is recorded as skipped. A scheduled turn has no time limit, like any chat turn, so a hung one is stopped with the chat's stop button.
- Each firing is credited with exactly the work items its own turn created: the ILD MCP server of a chat turn sends that turn's id with every API call, and a create whose outcome is unknown is counted as unresolved rather than guessed.
- Turn state is in memory only, so a restart fails every firing still running and writes a note into its chat.
