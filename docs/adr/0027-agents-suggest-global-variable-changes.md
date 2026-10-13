# ADR-0027: Global variables are human-owned; agents only suggest whole-value changes

A **Global Variable** feeds every loop prompt on the instance, so an agent that could write one would change what every later run is told, with nobody asked. Agents (loop runs and chat sessions) therefore never write a global variable. They read them with `get_global_variables` and **suggest** a new value with `propose_global_variable_change`; the suggestion, a **Global Variable Proposal**, applies nothing until a human approves it, and only the human surface can approve or reject one. This follows [ADR-0022](./0022-agent-edits-are-human-approved-proposals.md), with three differences that come from what a global variable is.

- **A suggestion is the whole new value.** There is no append or patch: the human approves exactly the text they see, diffed against the current value, and cannot edit it first. A suggestion may name a variable that does not exist, which suggests creating it; no suggestion can delete one.
- **The snapshot is the variable's `UpdatedAt`**, or "missing" when there was no such variable. Unlike a work item's, a variable's `UpdatedAt` moves only when its value is written, so it is exactly the thing a suggestion must not overwrite. Approving is one compare-and-set: the variable is written only if its `UpdatedAt` still equals the snapshot (or, for "missing", by an insert that does nothing if the name now exists), and every other pending suggestion for it goes **Stale** in the same transaction. A human writing the variable on the page makes its pending suggestions Stale in the same transaction as the write.
- **Suggestions live on the ILD instance**, not on the WorkItem Server ([ADR-0025](./0025-workitem-server-contract-is-tracker-mappable.md)), and only reference the run or chat that made them. Deleting that run, its work item or the chat leaves its pending suggestions pending, to be decided on the Global Variables page, because unlike an edit proposal a suggestion has a place to be decided that does not depend on its source.

## Considered options

- **Let agents write global variables directly.** Rejected: one agent's conclusion would silently become every run's instruction.
- **Append or patch suggestions.** Rejected: what is applied would differ from what the human read, and two patches could each look harmless and combine into something nobody approved.
- **Let the human edit a suggestion before approving.** Rejected: the approved text would no longer be the agent's, and the agent would be told its suggestion was approved. The human rejects it and edits the variable on the page instead.

## Consequences

- One source holds at most one pending suggestion per variable: a newer one from the same run or chat supersedes its earlier one, and the database enforces it. A superseded suggestion was never decided, so it is never announced.
- A suggestion longer than the `globalVariables.maxLength` setting is refused when it is made, with its length and the limit, so the agent can consolidate and try again in the same turn. Approve does not check the limit again.
- A chat learns the decisions on its next turn, as for edit proposals; a loop run reads them with `list_global_variable_proposals`.
