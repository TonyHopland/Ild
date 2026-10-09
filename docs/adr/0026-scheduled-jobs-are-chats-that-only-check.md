# ADR-0026: Scheduled jobs are chats that only check

Some checks should run on a timer and report what they find: a weekly retro of how the loops did, backlog grooming, upstream releases of the agent CLIs, outdated dependencies, flaky tests, docs drift. A **Chat Schedule** runs such a check by starting a chat turn of its owner's with a set prompt on a cron. A chat already runs turns on the server with no browser connected, with the same adapters, agent isolation and egress proxy as a loop run, and has the ILD tools a check needs. So a schedule adds a trigger, not a new kind of run.

A scheduled job **only checks: it never changes code.** When something needs changing it creates a work item, and the change goes through the normal loop with its own run, branch and pull request. A chat already has no path to change code: it has no branch, cannot start runs ([ADR-0011](./0011-context-aware-chat.md)), its pull-request tools only queue writes that a run's PR node posts, and the items it creates land in Backlog. A scheduled chat may edit only the items its own chat session created, like any chat ([ADR-0022](./0022-agent-edits-are-human-approved-proposals.md)).

Schedules, their firings and the link from a chat to the schedule that started it live on the Ild instance; the WorkItem Server knows nothing of them ([ADR-0025](./0025-workitem-server-contract-is-tracker-mappable.md)). A firing records when it fired, in which chat and how it went, but not which items it filed: the chat transcript is the record of what it did.

## Considered options

- **A scheduled loop run per job.** Rejected: a loop run belongs to a work item and works towards a branch and a pull request, which is exactly what a check must not do, and a check has no item to belong to.
- **Credit each firing with the work items it created**, passing the turn's id through the adapters and the MCP server. Rejected: the transcript already shows them, and the plumbing would reach every adapter for a list nobody needs to query.

## Consequences

- The global scheduler pause stops schedules too; Run now, a human action, still works while paused.
- Firings never queue: one that comes due while the schedule's previous turn still runs, or while its chat is busy, is skipped and says why, and missed firings merge into one.
