# ADR-0036: ILD from its image always runs agents in containers; local runs are for `dotnet run` only

[ADR-0032](./0032-run-host-interface.md) put where a run lives behind `IRunHost`, with `local` as the default and `ILD_RUN_HOST` choosing `container` ([ADR-0034](./0034-container-run-host.md)) or `kubernetes` ([ADR-0033](./0033-kubernetes-run-host.md)). Chats and the provider sign-in have since moved into containers and pods too. That left two complete ways of containing an agent: a container of its own, and a second Linux user inside ILD's container ([ADR-0014](./0014-agent-uid-isolation.md)), with its own preview rules ([ADR-0016](./0016-preview-runs-as-the-agent.md)), its own firewall ([ADR-0019](./0019-agent-egress-through-in-container-proxy.md)), shared groups, ambient capabilities and `NET_ADMIN`. Every change to how an agent starts had to be made, and tested, twice. ILD now has one way of containing an agent, the container.

## Decision

- **The run host is not a setting.** `ILD_RUN_HOST` is gone. ILD started from its image (which sets `ILD_IMAGE=1`) runs every loop run, chat and provider sign-in in a container of its own, or in a pod when the image runs in a Kubernetes cluster (`KUBERNETES_SERVICE_HOST` is set). ILD started any other way, which is `dotnet run` by a developer or by a preview of ILD's own repository, runs them as its own processes.
- **`local` is for development only.** It runs the agent as ILD's own user, with no second user and no firewall, and with no egress proxy unless `ILD_NETWORK_PROXY_PORT` is set (then only the agents that honour `HTTP_PROXY` go through it). It is not a way to deploy ILD.
- **The agent uid split is removed.** The image has one user, `ild`. The entrypoint no longer creates shared groups, agent-readable directories, firewall rules or ambient capabilities, and drops to `ild` with `gosu`. Compose no longer grants `NET_ADMIN`. On the first start after an upgrade, the entrypoint gives ILD back the files the agent user left in `/data` and `/worktrees`.
- **ILD's image is slim.** It has no .NET SDK, Chrome or second user; it keeps git, and Node with npm, which install the coding agents ILD hands to runs ([ADR-0035](./0035-run-toolchains.md)). `WITH_NODE`, `WITH_DOTNET_SDK` and `WITH_CHROME` are gone. `ild-runner-full` is no longer built: runs get their toolchains from the store, and a Kubernetes cluster needs a ReadWriteMany claim for it (`ILD_K8S_TOOLS_CLAIM`), or a runner image of the operator's own with `ILD_RUN_TOOLCHAINS=off`.
- **Compose is the container setup.** `docker-compose.yml` mounts the engine's socket, named by `ILD_CONTAINER_SOCKET_HOST` (it refuses to start without it), and builds the runner image. The separate `docker-compose.container-runs.yml` overlay is gone.

## Considered options

- **Keep `local` as an option for deployments without an engine.** It keeps ILD runnable where no socket or cluster can be given to it, but it is the second containment model this decision removes, with its firewall, groups and capabilities, and the agents in it share ILD's kernel namespace and network.
- **Keep the setting but default it to `container`.** One fewer surprise for an operator who reads the docs, but it leaves every code path in place and a setting whose only safe value is the default.
- **Detect any container, not ILD's image.** A preview of ILD's own repository runs `dotnet run` inside a run's container, where it must run its agents locally; only ILD's image says that ILD is deployed.

## Consequences

- Every deployment needs a container engine socket or a Kubernetes cluster. Compose needs `ILD_CONTAINER_SOCKET_HOST` in `.env`. A rootful engine's socket is root-equivalent, as [ADR-0034](./0034-container-run-host.md) says.
- A run is isolated from ILD, its database and its secrets by a container boundary rather than by a uid, and the egress limits are enforced by the run's network, which reaches only ILD.
- ADR-0014, ADR-0016 and the firewall of ADR-0019 describe the earlier design. The egress proxy, its lists and its log remain.
- Developing ILD with `dotnet run` runs agents unconfined, as before when `AGENT_USER` was empty.
