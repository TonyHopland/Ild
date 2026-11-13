# ADR-0033: A loop run is a pod, owned by one object and swept by label

> **Status: amended by [ADR-0036](./0036-runs-always-in-containers.md).** The run host is no longer a setting: ILD's image always uses containers, or pods in Kubernetes, and `local` exists only under `dotnet run`.
>
> It also said that scheduled jobs and the provider sign-in terminal stay local, and that pods get DNS egress. None of that holds: the sign-in terminal runs in a container or pod of its own, scheduled jobs are chats ([ADR-0026](./0026-scheduled-jobs-are-chats-that-only-check.md)) and run in the chat container or pod, and a run's or chat's pod has no DNS unless network mode `off` adds its `-open` policy (names are resolved by the egress proxy).

[ADR-0032](./0032-run-host-interface.md) put where a run lives behind `IRunHost`. `ILD_RUN_HOST=kubernetes` is the second host: each loop run gets a pod of its own, so runs are isolated from ILD and from each other and the scheduler spreads them across nodes.

## Decision

- **A pod per loop run, created at Start and deleted at Cleanup.** It stays up while a Human node waits, so previews keep working. There is no pause and resume. ILD keeps the graph, the human nodes, the database and the UI; the pod only executes. Chats, scheduled jobs included, run in a shared pod of their own, and the provider sign-in terminal in a short-lived pod of its own (see ADR-0034, which also covers the container host's chats): a routing host sends each call by the path it names, which for a run's pod is below `/ild-run/<run id>` and for a chat's below `/ild-chat/<chat id>`, and by the run it is for.
- **Commands are exec streams.** Each node's command is one exec over a websocket (the v5 channel protocol, which can close stdin), and the terminal is an exec with a TTY behind the same PTY interface. A dropped stream fails the node ([ADR-0017](./0017-shutdown-halts-the-in-flight-ai-node.md)). Kubernetes does not reliably kill the process when a stream closes, so each command records its process group under a name in the pod, and killing leftovers before a retry is an exec that kills those groups and waits.
- **Code goes in and out as bundles over exec.** The pod never holds the git credential or the mirror. The model gateway is reached at ILD's Service with a placeholder credential. Files the launch reads (MCP configs) are copied into memory in the pod and the launch is pointed at the copies.
- **Nothing is assumed about the cluster.** The volume is one `ReadWriteOnce` claim per run, whose class and size are settings, with the cluster's default class when unset. ILD warns at startup when the class does not delete its volumes with their claim. Pod requests are low and limits generous, so waiting runs do not fill the scheduler's view; ILD's run concurrency cap protects against memory overload. Longhorn gets a documented optional class, and no code.
- **Isolation is a NetworkPolicy per run**, plus no service account token, no capabilities, non-root and `RuntimeDefault` seccomp. Egress goes only to ILD (gateway, MCP endpoint, egress proxy); ingress only from ILD. The pod has `dnsPolicy: None` and no DNS egress rule, except while network mode `off` gives it the direct route ([ADR-0036](./0036-runs-always-in-containers.md)). Anything else an agent connects to goes through the egress proxy and its limits, which therefore listens on all interfaces under this host.
- **Cleanup is two layers.** The run's ConfigMap is the owner object, and the claim, the policy and the pod carry an owner reference to it, so ILD deletes one object and garbage collection removes the rest. A sweeper at startup and on a timer deletes labelled objects (`app.kubernetes.io/managed-by=ild`, `ild/instance`, `ild/run`) whose run is finished or no longer exists. It only touches its own `ild/instance`, so two installs can share a namespace. A killed ILD leaves nothing behind after the next sweep.
- **RBAC is a namespaced Role** on pods, pods/exec, claims, ConfigMaps and network policies in the run namespace. Reading StorageClasses for the warning is an optional ClusterRole.

## Considered options

- **Pause and resume a pod between nodes.** Rejected: a run waiting for a human is idle, and what it needs while it waits (previews, files) is the pod.
- **A Kubernetes Job per node.** Rejected: the worktree and the processes a preview starts have to outlive a node.
- **Deleting every object from ILD.** Rejected for the run's normal end: one owner means one call, and a crash in the middle cannot leave half of it. The sweeper covers what an owner reference cannot, an ILD that died before it deleted anything.
- **Talking to a node-local runtime (a container socket).** Rejected: it ties the host to one runtime; the Kubernetes API is the generic seam.
