# ADR-0032: Where a run's processes and files live is one interface, the run host

> **Status: amended by [ADR-0036](./0036-runs-always-in-containers.md).** The run host is no longer a setting: ILD's image always uses containers, or pods in Kubernetes, and `local` exists only under `dotnet run`. It is also amended, below, so that callers depend on role views of the one host.

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

## Amendment: one host, role views of it

`IRunHost` had grown to about 47 members, each caller used a small slice, and every capability a backend did not have everywhere arrived as a default-bodied member, a side interface a caller found by downcasting (`is IIsolatedHostInfo`, `is IRunHostShutdown`, `is ISignInTerminalHost`) or a public member of the isolated host reached as `routing.Isolated`. The decision above stands: there is **one host and one backend, chosen once** (`RunHostSelection`, built where the setting is read), and "a host per call site" is still rejected. What changes is what a caller sees. "One interface" now means **one host that implements narrow role interfaces**, and a caller depends on the role it uses (`RunHostRoles.cs`):

- **Roles of every host:** `IRunLifecycle`, `IRunCommands`, `IRunFiles`, `IRunHolders`, `IPreviewHost`, `IChatHost`, `IRunBundles`, `IRunTerminals`, `IRunLeftovers`. A caller that uses several together takes one named composite (`IRunWorkspace`, `IRunChats`, `IRunChatFolders`, `IRunReclaim`, `IRunRepositories`, `IRunShells`), which only puts roles together. `IRunHost` is all of them, and the one registration of the host; each role is a forwarding registration of it.
- **Roles only some backends have:** `IRunHostOperator` (what the settings page and network guard ask, and the stop of what the host keeps), `IRunSweep` (the timer-driven sweep of pods and containers), `ISignInTerminalHost` (the sign-in container) and `ILocalTerminalCommands` (the routed command line a terminal on ILD's machine starts with). A backend that lacks one has none: `RunHostSelection` carries it as null, or as a host that answers nothing for the operator view. No caller tests the host's type, and none reaches a member of the isolated host.
- **File reads** are `ReadTextFileAsync` (whole, under the read limit), `OpenFileForReadAsync` (a stream, shared with writers; null for a directory or nothing) and `ReadFileRangeAsync` (a range of a file too big to read whole). `OpenReadAsync` is gone: it was the same read.
- **`RouteTerminalCommand`** is no longer on the isolated host, which could only refuse it.

Adding a capability is an edit to the role that owns it and to the backends that have it, not to every implementer.
