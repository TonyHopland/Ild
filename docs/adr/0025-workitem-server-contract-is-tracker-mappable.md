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

- Several existing fields do not fit this model: `CreatedByLoopRunId`, `CreatedByChatSessionId`, `HumanFeedbackActions`, `LastHeartbeatAt`, the AI provider override, the branch overrides (`BranchNameOverride`, `BaseBranchOverride`) and Work Item Edit Proposals ([ADR-0022](./0022-agent-edits-are-human-approved-proposals.md)). They are subject to a later audit and are left as they are by this decision. `ConversationJson` is being removed by #286.
- New features that need per-work-item Ild state add it to `ILD.Data`, keyed by work item id, rather than to the WorkItem Server.
