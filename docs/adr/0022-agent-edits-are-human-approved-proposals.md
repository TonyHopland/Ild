# ADR-0022: An agent edits a work item it did not create only through a proposal a human approves

Agents (loop runs and chat sessions) may edit or delete only the work items their own session created: `update_workitem` and `delete_workitem` answer 403 for anything else, so a human stays in control of the backlog. That rule stays. What changes is that an agent may now **propose** an edit to any work item — its title, description, tags and branch overrides — through `propose_workitem_edit`. A proposal applies nothing. It waits, as a **Work Item Edit Proposal**, until a human approves or rejects it, and only the human surface can do either: the agent surface has no route that decides one. Status transitions and deletes cannot be proposed.

Proposals live on the WorkItem Server beside the items they edit ([ADR-0001](./0001-standalone-workitem-server.md)), with a snapshot of the five editable fields taken when the proposal is made. Approving is one atomic compare-and-apply on the server: the item is written only if those fields still equal the snapshot, in the same statement that writes it, and every other pending proposal on the item goes **Stale** in the same transaction. A proposal whose item a human edited since goes Stale instead of overwriting the edit.

## Considered options

- **Let agents edit any item directly.** Rejected: an agent could quietly rewrite work a human planned, and nothing would ask the human first.
- **Approve by reading the item, comparing, then calling the ordinary update.** Rejected: a human edit landing between the compare and the write is overwritten, which is the one outcome approval exists to prevent.
- **Stale when the item's `UpdatedAt` moved.** Rejected: status transitions, conversation turns, PR records and dependency edits all move it, so a proposal on any active item could never be approved. Staleness is about the five fields a proposal can change.
- **Block the proposing loop run until a human decides.** Rejected: the run would hold its concurrency slot waiting on a human for an edit to some other item. A run proposes and carries on; it can read the outcome with `list_workitem_edit_proposals`.

## Consequences

- A chat learns the decisions on its proposals on its next turn, in the prompt; the notice is marked delivered only once a turn reached the agent, so a turn that failed to launch announces it again. A loop run has no next turn, so it reads outcomes on demand.
- Humans find pending proposals on the board card's count and in the work item's detail view, and a chat shows its own proposals inline.
- Each work item holds at most 20 pending proposals, so an agent cannot flood one item.
