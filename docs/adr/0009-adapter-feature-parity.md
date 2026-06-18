# ADR-0009: Every agent adapter stays feature-complete

An AI node resolves its `IAgentAdapter` from `AiProvider.Type` ([ADR-0007](./0007-ai-execution-delegated-to-adapters.md)), and the provider — including which adapter is the default — is configuration, changeable between runs. A loop author therefore cannot assume a specific adapter backs their AI nodes. To keep that substitution safe, **every registered adapter implements the same session capabilities**: a loop must behave the same whichever CLI runs it.

Concretely, `claude-code`, `pi`, and `opencode` all implement the managed-session lifecycle (snapshot → restore → bind) **and** the session-copy/fork primitive. Forking seeds a copy of a source session's transcript under a new session id, continues on the copy, and never writes the source. Each CLI's resume mechanics differ — claude-code `--resume` appends to the session file, so a true fork must seed the copy under a _new_ id rather than resuming the source in place — so the shared `CliAgentAdapterBase` owns the copy primitive (`ForkSessionSnapshotAsync` + the id-rewrite in `RewriteSessionTranscript`) and every CLI adapter inherits it. A new adapter that skipped the fork primitive would silently break any loop that forks the moment it became the default.

## Consequences

- Adding a capability to one CLI adapter means adding it to all of them (or to the shared base) before it can be relied on; a half-implemented capability is a parity bug, not a per-adapter feature.
- The fork copy lives in the base class keyed by run + adapter name + session id, reusing the existing `IAdapterSessionSnapshotStore`. No schema change is needed: AI-node session bindings already share one namespace (keyed by the node-type string `"AI"`), so a fork-from can reference a session another AI node bound.
- Session ids are treated as opaque, unique tokens, so the id rewrite is a textual replace that works across every adapter's snapshot format (claude wrapped JSONL, pi raw JSONL, opencode export JSON) without parsing each shape.
