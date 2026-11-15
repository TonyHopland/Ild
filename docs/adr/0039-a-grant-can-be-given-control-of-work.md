# ADR-0039: A grant can be given control of work

Amends [ADR-0037](0037-external-agents-use-expiring-revocable-grants.md). An AI session outside ILD could file a work item with a `Full` grant but could not start it or answer a human-feedback stop, so an operator had to be at the keyboard for every item. The operator wants a level that lets the outside AI drive an item from Backlog to done.

## Decision

- **A third level, `Control`.** `ExternalAgentGrantScope.Control = 2` is chosen when a grant is created or renewed. Stored values stay `Full = 0` and `ReadOnly = 1`, so every existing grant keeps its level. `Full` meant "everything an agent may do", which would now be false; it is named for what it allows instead: the levels read **Read only**, **Read, create and propose** (`Full`) and **Control of work (start and answer)** (`Control`).
- **What it adds.** Start a work item (the operator's start: a Ready item, past its repository's parallelism limit but not the instance's concurrency cap), move one into the work queue, and answer a human-feedback stop: input, respond, reject, and pick an edge.
- **Through the agent surface, not the user routes.** New routes `POST /api/v1/agent/workitems/{id}/start`, `.../queue` and `.../human-feedback/{input,respond,reject,edge}`, and MCP tools `start_workitem`, `queue_workitem`, `human_feedback_input`, `_respond`, `_reject` and `_edge`. The routes answer 403 unless the caller is a grant at `Control`, so ILD's own run and chat agents, `ReadOnly` and `Full` grants never reach them. The MCP tools are not served to ILD's own agents over stdio and are listed over `/mcp` only to a `Control` grant. The grant is still refused on `/api/v1/workitems/...`.
- **Any work item, not only its own.** ADR-0037 limits a grant to what it created because nobody present is driving it. For this level the operator asked for full control of the work it is handed, and a start or an answer needs an item someone else created: filing and starting are usually done by different sessions. The operator's choice of level, the seven-day cap, revocation, the rate limit and the audit bound the exposure. This departs from the own-items limit on purpose; `Full` keeps it.
- **What stays user-only.** Moving an item to any status other than the work queue (including Done), `link-pr` and merge, deciding proposals, Settings, AI providers, backup, sessions and the grant routes.
- **Proposals unchanged.** A `Control` grant still edits only its own items and proposes edits to others; it cannot decide a proposal.
- **Attribution.** A start, a queue move and an answer are recorded in the change history with actor kind `ExternalAgent`, the grant's name and its id. An answer is attributed through the move back to Running that it causes.
- **Discovery.** `GET /api/v1/agent/_about` lists the new routes, marks them Control-only, and names the fields of the create and update requests (including `tags`, which picks the loop template).
- **Spend: no new cap.** Starting work spends AI provider money, which the per-minute request budget does not bound. Starts are already limited by the instance's concurrency cap and one run per item, and the grant ends in at most seven days or when revoked. A human-set cap (for instance starts per grant per day) can follow if this proves too loose.

## Unchanged

Header-only token, mandatory expiry of at most seven days, revocation, the per-grant rate limit and the audit of every request apply to the new level as to the others.
