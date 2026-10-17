# ADR-0013: Retained chat history — many chats per user, deleted only on demand

The chat bubble ([ADR-0010](./0010-standalone-chat-session.md)) was one ephemeral chat per user: starting a second chat threw, "End chat" hard-deleted the session, and a `ChatSessionRetentionSweeper` reclaimed anything idle past a retention window. We replace that with **retained history**: a user keeps many chats indefinitely, browses them in the bubble, resumes any with its full transcript and continued agent session, and a chat is deleted **only** by an explicit per-chat delete or "delete all" — never automatically.

Two parts of this shift were deliberate trade-offs.

**We dropped the unique index on `ChatSession.UserId` rather than model an explicit "active chat" row.** Many retained chats need a one-user-to-many shape, so the unique constraint goes and the index becomes a plain lookup for the history list. We kept the single-_active_-chat assumption that SignalR groups and the agent-session binding already rely on: "resume" simply makes the selected chat the live one (the client joins its hub group and leaves the previous). Concurrent _active_ chats would mean per-chat turn runners and group multiplexing — a larger change deferred until there is demand, not paid for speculatively here.

**Names are derived from the first user message, not typed by the user and not (yet) LLM-generated.** A chat is named once, on its first turn, by collapsing whitespace and truncating that message to a sensible length. We rejected prompting the user for a title (friction on every new chat) and an LLM-generated one-liner (an extra model round-trip and failure mode) for the first cut — first-message truncation is deterministic, free, and good enough to tell chats apart in the list. An LLM title remains an easy later enhancement behind the same `Name` column.

## Consequences

- **No inactivity deletion.** The `ChatSessionRetentionSweeper` and the `IdleRetentionPeriod`/`SweepInterval` options are removed; chat scratch dirs and rows persist until the user deletes them. Per-chat delete and delete-all reuse the existing hard-delete (cascades messages + adapter snapshots, removes the scratch dir).
- **Deletes are scoped by user.** `GetById`/`Delete` take both the chat id and the user id so one user can never resume or delete another's chat; the API authorizes every per-chat action this way.
- **"Back" is not "End".** Leaving a conversation returns to the list and retains the chat; it makes no server call beyond what each turn already persists.

## Amendment: opt-in deletion of inactive chats

Inactivity deletion is back, but only for those who ask for it. The
`chat.autoDeleteInactive` app setting is **off** by default, so a chat is still
kept until someone deletes it unless an operator turns it on. While it is on,
`ChatInactivitySweeper` deletes every chat, of every user, that is not a favorite
and has had no new message for `chat.autoDeleteInactiveDays` (default 14), or,
with no messages, has existed that long. Only a message is activity: a rename, a
star or a read marker does not keep a chat. A favorite is spared for as long as it
is starred; unstarring it adds no grace period, so an old one goes on the next
sweep. The delete is the same hard-delete as the per-chat one, run under the
chat's turn gate only when no turn is in flight, so a busy chat is skipped rather
than interrupted, and one that was written to or starred after it was picked is
kept. The "deletes are scoped by user" consequence still holds for every request
a user makes; the sweeper is the one system caller that is not.
