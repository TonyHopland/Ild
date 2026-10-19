# ADR-0025: The WorkItem Server API is a contract an external tracker can implement

`ILD.WorkItemServer` is a self-contained server with its own database, and it stays one ([ADR-0001](./0001-standalone-workitem-server.md)). Its API, though, is a **contract**, and the built-in server is only one implementation of it. Another may implement it as a thin adapter over an external planning tool, such as GitHub Issues or Azure DevOps Boards. We design the contract as if that is the norm. Every field or endpoint has to map onto such a tool: one the tool has no native concept for is expensive or impossible for an adapter to provide. So the contract stays small, and Ild-specific data lives on the Ild instance.

## What the contract may carry

Only concepts that typical trackers support natively:

- title and description
- status / state
- priority
- tags / labels
- dependencies (GitHub issue relationships, Azure DevOps work item links)
- linked pull requests (GitHub development links, Azure DevOps artifact links)
- attachments
- the claiming / assignment that instances need to coordinate with each other

## What it may not

Everything Ild-specific lives on the **Ild instance**, keyed by work item id: loop runs, conversations, schedules, chat sessions, run settings and similar state. When a feature needs to associate Ild data with a work item, it stores that link on the Ild side. It does not add a field, column or endpoint to the WorkItem Server contract. That the built-in server _could_ store something easily is not a reason to put it there.

## Considered options

- **Treat the built-in server as the home for all work-item state.** Rejected: each such field is one more thing an adapter over an external tracker cannot provide, so the contract would quietly come to depend on the built-in server.

## Consequences

- Several existing fields do not fit this model: `CreatedByLoopRunId`, `CreatedByChatSessionId`, `HumanFeedbackActions`, `LastHeartbeatAt`, `RepositoryId` (a GitHub issue lives in one repository, but an Azure DevOps work item belongs to a project, not a repository), the AI provider override, the branch overrides (`BranchNameOverride`, `BaseBranchOverride`) and Work Item Edit Proposals ([ADR-0022](./0022-agent-edits-are-human-approved-proposals.md)). They are subject to a later audit and are left as they are by this decision. `ConversationJson` is being removed by #286.
- New features that need per-work-item Ild state add it to `ILD.Data`, keyed by work item id, rather than to the WorkItem Server.

## Amendment: change history

The contract now carries a work item's change history, because trackers keep one natively (GitHub issue timelines, Azure DevOps work item history). Each entry is one field's new value in one change: when it happened, who made it as a user name, which field, the new value, the change it belongs to (so the fields one save changed read as one event), and whether it records the item's creation, the values it had when history began, or a later change. The previous value of a field is the value of its previous entry, paired by the server when it answers. A status change may carry a free-text reason; it never decides whether the change is allowed.

- Every mutating request names who it acts for in an `X-ILD-Actor` header, and is refused without one. The server records it without interpreting it.
- The header names the user and a kind (human, agent, chat, system), and for every kind but human a detail naming what acted on the user's behalf; it may name the work item a loop run was working on. A history read returns the user name for certain and the kind, detail and work item only as optional, opaque attribution: an adapter over a tracker that keeps no such notion leaves them out, and a client shows nothing for an absent one.
