# ADR-0001: Standalone WorkItem Server as the work-item source of truth

Work-item state, dependencies, tags, repository association, and claim semantics live in a separate `ILD.WorkItemServer` process reached over HTTP with an API key — not inside `ILD.Api`. We did this so multiple ILD instances can coordinate against one authoritative store and claim items atomically without stepping on each other; embedding the data in a single ILD host would have tied the source of truth to one instance's lifecycle. Each ILD instance treats the server as remote and heartbeats the items it is actively working.

Refined by [ADR-0025](./0025-workitem-server-contract-is-tracker-mappable.md): the server's API is a contract that an adapter over an external tracker may implement, so any new contract surface must be something such trackers support natively. Existing fields that are not, repository association among them, are listed there for a later audit.

## Consequences

- ILD-local state (loop runs, templates, providers, event logs, and anything else Ild-specific about a work item) stays in `ILD.Data`; the two stores are separate databases with their own lifecycles.
- Every work-item read/write is a network call, so `WorkItemManager` is remote-backed and callers must tolerate latency and unavailability.
