# ADR-0032: Where a run's processes and files live is one interface, the run host

> **Status: amended by [ADR-0036](./0036-runs-always-in-containers.md).** The run host is no longer a setting: ILD's image always uses containers, or pods in Kubernetes, and `local` exists only under `dotnet run`.

The orchestrator used to start the agent's processes, open its terminals, read its transcripts and find its previews with code that assumed one machine and the uid split of [ADR-0014](./0014-agent-uid-isolation.md). To run the agent somewhere else, a container or a Kubernetes pod, that assumption has to sit behind one seam.

## Decision

- **One interface, `IRunHost`.** It covers creating and deleting a run's environment, starting a command with streams and killing it, killing everything still running for a run, opening a PTY, moving code in and out as bundles ([ADR-0016](./0016-preview-runs-as-the-agent.md) keeps previews on it too), reading and writing the files a CLI leaves, the address the preview proxy forwards to, the base address of the model gateway the CLI is given, and listing and deleting what finished runs left behind. Every agent adapter, the Cmd node, the preview spawns and both terminal services go through it.
- **`local` is today's behaviour**, and the default: the agent's processes are children of the orchestrator that cross to the agent uid, and its files are the worktree and the scratch and agent-read roots. The code that spawns, routes through `setpriv`, opens the PTY or reads the agent's files as the agent lives under `RunHosts/Local` and nowhere else. A test scans the sources for it.
- **The backend is an explicit setting**, `ILD_RUN_HOST` = `local` | `container` | `kubernetes`, default `local`. Nothing is detected from the environment. `kubernetes` ([ADR-0033](./0033-kubernetes-run-host.md)) and `container` ([ADR-0034](./0034-container-run-host.md)) are built.
- **A disconnect still halts the node** ([ADR-0017](./0017-shutdown-halts-the-in-flight-ai-node.md)). Before a halted node resumes or a node is retried, the engine asks the host to kill whatever is still running for that run.
- **Files are read through the host, never by path**, so a host whose files are not on the orchestrator's disk works. The one check that has to open a worktree's `.git` pointers itself, whether a path is a clone this orchestrator made, sits in `RunHosts/Local`.

## Considered options

- **Detecting the backend** (a Kubernetes service account, a container runtime socket). Rejected: it makes a misconfigured deployment run somewhere nobody chose, with different trust.
- **A host per call site.** Rejected: five adapters, the Cmd node, previews and terminals would each pick a backend; one interface keeps the choice in one place.
