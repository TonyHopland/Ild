# Verifying the container run host on a real engine

The tests run against a fake Docker API, which cannot show that Podman or Docker really isolate, route and clean up a run's objects. This is checked once by hand, first on **rootless Podman** (the supported setup), then on Docker. It takes about 30 minutes. `<run>` below is the run's id without dashes; `<instance>` is `ILD_CONTAINER_INSTANCE` (`default` if unset). Use `docker` for `podman` in the second pass.

## 1. Set up

1. Enable the socket:

   ```sh
   systemctl --user enable --now podman.socket
   ls $XDG_RUNTIME_DIR/podman/podman.sock
   ```

   Expected: the socket exists.

2. Set `ILD_CONTAINER_SOCKET_HOST` in `.env` to your engine's socket ([Which socket](../deployment.md#which-socket); a Podman machine's is the path inside its VM), and `GIT_AUTHOR_NAME`, `GIT_AUTHOR_EMAIL`, `GIT_COMMITTER_NAME` and `GIT_COMMITTER_EMAIL`. Start the stack with `podman compose up -d --build`, which also builds the runner image. Expected: `podman images localhost/ild-runner` lists the image.

3. Read ILD's log. Expected: no startup failure and no warning about the socket, and `Container engine socket /run/container.sock: ild can open it through group …`. Open **Settings > General**: no "Run host" warning.
4. Tag the test work item `HIL`. A Develop run then stops at **Approve PR**, before the PR node pushes a branch and opens a pull request on the repository.
5. Stop the engine's socket (`systemctl --user stop podman.socket`) and reload **Settings > General**. Expected: a "Run host" card says ILD cannot reach the engine, and why. Start the socket again and restart ILD; the card is gone.

## 2. A full Develop run

Start a Develop loop on a small work item and watch the objects in another terminal:

```sh
watch -n1 'podman ps -a --filter label=app.kubernetes.io/managed-by=ild; podman volume ls --filter label=app.kubernetes.io/managed-by=ild; podman network ls --filter label=app.kubernetes.io/managed-by=ild'
```

| Step                       | Expected                                                                                                                                                                                                                                                                                                                                                                           |
| -------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Run reaches its Start node | One container, one volume and one network, all named `ild-run-<run>`, labelled `ild/instance=<instance>` and `ild/run=<run>`. `podman network inspect ild-run-<run>` shows `"internal": true`                                                                                                                                                                                      |
| Container comes up         | `Up` within a few seconds. `podman inspect ild-run-<run> --format '{{.Config.User}} {{.HostConfig.CapDrop}} {{.HostConfig.SecurityOpt}}'` prints `10001:10001 [ALL] [no-new-privileges]` (Podman lists the capabilities it dropped by name instead of `ALL`, and rootless Podman may also list its seccomp setting)                                                                |
| Agent turns                | The agent node completes and the event log shows the output as it streams. `podman exec ild-run-<run> ls /ild-run/<run>/repo` shows the checkout, and `ls -ld /ild-run` inside is owned by uid 10001                                                                                                                                                                               |
| Limits                     | `podman inspect ild-run-<run> --format '{{.HostConfig.Memory}} {{.HostConfig.NanoCpus}}'` prints `8589934592 4000000000`. **If the container fails to start on a rootless engine with `crun: open 'memory.max'` (the cgroup controllers are not delegated, as on a Podman machine), set `ILD_RUN_CPU_LIMIT=0`, `ILD_RUN_MEMORY_LIMIT=0` and `ILD_RUN_PIDS_LIMIT=0`** and report it |
| Human reply                | The run waits at the Human node; the container stays `Up`. Answer in the UI and the next node runs in the same container                                                                                                                                                                                                                                                           |
| Preview                    | Start the preview from the work item's Preview tab; the preview URL loads                                                                                                                                                                                                                                                                                                          |
| PR push                    | The PR node pushes the branch and opens the PR. `podman exec ild-run-<run> env` shows no token, and `git -C /ild-run/<run>/repo remote -v` shows the remote URL without credentials                                                                                                                                                                                                |
| Cleanup                    | When the worktree is cleaned up, the three `--filter label=ild/run=<run>` listings print nothing within a minute                                                                                                                                                                                                                                                                   |

## 3. Isolation

From inside a run's container (`podman exec -it ild-run-<run> bash`), each of these must fail or time out:

- `curl -m 5 https://example.com` (the internet; skip this one while the network mode is `off`, which gives the run a direct route on purpose)
- `curl -m 5 http://<another run's container ip>:<any port>` (another run; start a second run to get one)
- `curl -m 5 http://<host's LAN address>:22` or any other service of the host except ILD
- through the proxy (the run's `HTTP_PROXY`), `CONNECT <machine LAN IP>:<a port the stack publishes>` (ILD's own or the WorkItem Server's) and `CONNECT <any other private address>:<port>`: both are refused with `protected: Private and cluster-internal addresses` in the Network log
- `id` prints uid 10001, and `grep Cap /proc/self/status` shows `CapEff: 0000000000000000`
- `ls /var/run/*.sock` finds no engine socket

And with a forward declared to one of those addresses and port, a `CONNECT` and a plain `GET http://<host>:<port>/` through the proxy are allowed.

And these must work, in the work item's terminal (a shell from `podman exec` has neither variable): `curl -m 5 -s -o /dev/null -w '%{http_code}\n' $ILD_API_URL/api/v1/health` prints `200`, and a command that uses the proxy variables the agent is given (`env | grep -i proxy`) reaches an allowed host through ILD's egress proxy.

## 3b. Toolchains

Use a work item on a repository with a `package.json` and an `.nvmrc` (say `20`), and an AI provider whose agent ILD has installed.

| Check            | Expected                                                                                                                                                                                                                                                                                                                                                           |
| ---------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| First run        | ILD's log says `Run <run> gets node@20 …` and then `has node@20.x.y, <agent>@<version>`. While it installs, `podman ps --filter label=ild/role=tools` shows an `ild-tools-…` container; it is gone when the run starts. `podman volume ls` lists `ild-tools-agents`                                                                                                |
| In the run       | `podman exec ild-run-<run> sh -c 'node --version; command -v <agent>; echo $PATH'` prints v20, a path under `/opt/ild-tools/agents/<key>/<version>/wrap`, and a PATH that starts with the store's directories and has `$HOME/.local/bin` before the image's. `<agent> --version` matches Settings                                                                  |
| No .NET          | `podman exec ild-run-<run> sh -c 'command -v dotnet'` prints nothing for a node-only repository                                                                                                                                                                                                                                                                    |
| Read-only        | `podman exec ild-run-<run> touch /opt/ild-tools/x` fails. `podman exec ild-run-<run> npm install -g cowsay` succeeds, through the egress proxy, and `cowsay hi` works                                                                                                                                                                                              |
| Second run       | A second work item on the same repository starts without an `ild-tools-…` container                                                                                                                                                                                                                                                                                |
| Zones            | `podman volume ls` lists `ild-tools-agents`, `ild-tools-toolchains` and, after a `mise.toml` naming `"npm:prettier"`, `ild-tools-repo-<repository id>`. The run mounts them read-only at `/opt/ild-tools`, `/opt/ild-toolchains` and `/opt/ild-repo-tools`; a run of another repository has no `/opt/ild-repo-tools` content, and a chat mounts the first two only |
| Install scripts  | A repository tool whose `postinstall` writes under `/opt/ild-tools` fails: its installer cannot see that volume. `npm_config_ignore_scripts=true` is in the environment of the agents and toolchains installers, not the repository one                                                                                                                            |
| `run.tools`      | With `{"run": {"tools": ["python@3.12"]}}` merged to the base branch, the next run has `python3 --version` 3.12, and node as before                                                                                                                                                                                                                                |
| A failed install | With `.nvmrc` set to `99` on the base branch, the run fails at Start with mise's message, and no `ild-run-…` container is left                                                                                                                                                                                                                                     |

## 4. A killed ILD leaves nothing behind

1. With a run at a Human node, kill ILD hard: `kill -9 <ild pid>` (or `podman kill ild`).
2. The objects remain: the `podman ps -a`, `volume ls` and `network ls` listings still show the run's three objects.
3. Restart ILD **with that run finished or removed** (cancel the run first, or delete its row), or with a different run set. Expected within the first sweep (startup, or `ILD_CONTAINER_SWEEP_INTERVAL_MINUTES`): the run's container, volume and network are gone, and ILD's log says `Removed the leftover <run>, which is finished or gone`. `podman inspect ild --format '{{json .NetworkSettings.Networks}}'` no longer lists `ild-run-<run>`.
4. A run that is still unfinished (including a failed one that can be retried) keeps its objects across the restart. This is intended.
5. Objects of another instance survive: `podman run -d --label app.kubernetes.io/managed-by=ild --label ild/instance=other --label ild/run=00000000000000000000000000000001 --name other-probe alpine sleep 600`, restart ILD, and the probe is still there. Remove it afterwards.

## 5. The rootful warning

Point `ILD_CONTAINER_SOCKET_HOST` at a rootful socket (`sudo systemctl enable --now podman.socket`, then `/run/podman/podman.sock` with `ILD_CONTAINER_SOCKET_GRANT=1`; or `/var/run/docker.sock` of a rootful Docker) and restart ILD (`podman compose up -d`). Expected: the log has a warning that the engine is not rootless, and **Settings > General** shows a "Run host" card with the same text. Point it back at the rootless socket and the warning is gone.

Record in the work item: the engine and version for each pass, and anything in sections 2 and 3 that did not match.
