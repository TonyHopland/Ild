# ADR-0021: Client state is scoped to the entity it belongs to, and every message carries identity

A frontend screen that shows one entity after another (a work item, a chat, a run) keeps its state on the thing that owns it, not on a shared component that is reset or guarded on switch. The alternative, one long-lived instance patched with guards for each interleaving review finds, cost three consecutive features most of their review rounds. Each guard closed one interleaving and exposed the next. The rules below make the bad cases unrepresentable instead of detecting them afterwards. They bind the planner, the implementer and the reviewer on every change to client state.

**1. Scope state to the entity it belongs to.** A component, hook or cache that can show entity A and then entity B gets a fresh instance per entity: render it with React `key` set to the entity's id, and create per-entity caches keyed by that id. Do not reset shared state when the entity changes. Where a scope can make the bad case impossible, use the scope rather than a guard that notices the bad case after it happens.

**2. Give each act its own state.** When one screen serves two operations (an edit form and an answer pane, a save and a stop), each operation owns its own state: its own busy flag, error, staging list and in-flight request. Clearing, cancelling or finishing one must not be able to touch the other. A busy or error flag shared between two acts is a violation.

**3. Tag every in-flight operation with `(entityId, generation)`.** When a request starts, record the entity it is for and a generation that increases each time that operation restarts. A response settles only the operation whose `(entityId, generation)` it matches. A late response for a superseded operation or another entity is dropped. It is never applied to whatever happens to be on screen now.

**4. Put identity and order on every message that changes a client's belief.** A server event that can change what the UI thinks is happening carries the id of the thing it is about and a per-entity monotonic sequence. The client applies it only if the id matches and the sequence is newer than the last one it applied for that entity, and ignores everything else. No message clears client state unconditionally. A clear names what it clears and at which sequence.

**5. Never depend on one message arriving.** Broadcasts are hints. All client state must be recoverable from a snapshot read of the entity, and the client re-reads on reconnect and whenever it may have missed something. Order reads against each other and against in-flight writes, so an older snapshot can never overwrite a newer one: a read started before a write settled does not overwrite what that write returned. This holds for every hub. The frontend connection (`frontend/src/hooks/useSignalR.ts`) uses `withAutomaticReconnect()` without stateful reconnect, and SignalR does not replay messages sent while a client was disconnected or reconnecting, so a client misses events whatever the server does. On top of that, `SignalRRunNotifier`, `SignalRChatNotifier`, `SignalRLogNotifier` and `SignalRNetworkNotifier` (`ILD.Api/Configuration`) catch and log a failed broadcast by design instead of throwing.

## Considered options

**Guards on a shared instance** — generation counters, epochs, busy flags, abandoned outcomes added to one component that serves every entity. Rejected, because each guard fixes one interleaving and the next review finds the next one. The evidence:

- **PR #158 (work item #169), `WorkItemModalV2`:** one dialog served whichever item was open, with a staging hook shared by the edit form and the feedback pane. Eleven review rounds: uploads landing on the previous item, a batch joined by another item's save, a cancelled edit wiping an answer's files, a dialog dismissed mid-save. It ended with the dialog keyed by item id.
- **PR #159 (work item #176), `ChatBubble`:** one bubble served whichever chat was open, with send-error and stopping state on the component. Seven review rounds at the time of writing: a rejected send writing its error into another chat, a stop leaving another chat's Stop disabled, events applied to the wrong turn, a lost start event stranding a pending sentinel.
- **PR #157 (work item #173), provider-type comparisons:** the comparisons drifted between production and test doubles. It is the same shape one level down: an identity compared by a copy rather than carried.

## Consequences

- **Switching discards in-progress state.** A per-entity instance loses unsaved input, pending uploads and errors when the user moves to another entity. That is the intended behaviour in every case seen so far. Remounting also costs a render, which is cheap next to a review round.
- **Server events need an id and a per-entity sequence.** Adding them is an additive payload change, allowed under the add-only `/api/v1` policy ([ADR-0002](./0002-manual-api-versioning.md)).
- **The rule binds new and changed code.** Existing violations are fixed by their own work items. A finding against shared state is fixed by rescoping the state, never by adding another guard to it.
- **Plans and reviews check scope.** A planner states, for each piece of client state, what owns it and what it is keyed by. A reviewer rejects a guard where a scope would do.
