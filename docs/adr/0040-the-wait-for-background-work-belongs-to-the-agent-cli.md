# ADR-0040: The wait for background work belongs to the agent CLI

ILD used to look, when an agent's turn ended, for every process the agent had started that was still alive, park the node as "Waiting for ILD" until they had all ended, and wake the agent with a message about them. A dev server left over from verification (`npm exec vite`, `esbuild --service`) therefore held a work item for the whole wait although the agent never meant to wait for it.

The Claude Code CLI draws the line elsewhere: in headless mode (`claude -p`) it waits only for work the agent **registered** as background work (Bash with `run_in_background`, Monitor, background subagents and workflows) and ignores every other child process. While it waits it keeps the launch open, and when the work ends it gives the agent one more turn with the real outcome (exit status and output), which ILD's wake-up message could never carry. The task registry lives in that process; a later `--resume` does not get it back, so ILD could not ask about registered tasks once the CLI had exited.

## Decision

- **The wait belongs to the CLI.** ILD keeps no wait of its own and no list of background tasks. A launch is one or more turns, and the node completes when the CLI exits, carrying the last turn's output, the last turn's error state, the sum of the turns' tokens and the launch's final running cost.
- **Show, do not manage.** While the node runs, the UI lists the background tasks the CLI reports (`background_tasks_changed`, replaced on every event), on the running node, replayed to a client that reloads. The run stays Running: no park, no halt reason, no status. A provider or CLI version that sends no such events shows nothing.
- **Unregistered processes are not ILD's.** A process the agent started without registering it is not background work. ILD does not list it, wait for it or stop it. It ends with the run's container.
- **Bounds are the CLI's and the node's.** The node's own timeout ends the launch, and with it the tasks. The CLI's limits (30 minutes for a background command by default, a 10-minute idle cap for subagents, workflows and monitors) are left at their defaults; ILD adds no setting for them.
- **A known consequence.** An agent that registers a dev server as a background command and ends its turn keeps the node running until that command reaches its limit, and holds its AI-provider slot meanwhile. That is the CLI's defined behaviour and is not worked around.
- **Runs parked by the old wait** were converted once at startup into an ordinary halt for a person, with a reason saying ILD no longer waits and the node can be resumed. The halt reason's number is not reused. Old timelines keep rendering.

## A deliberate exception to ADR-0009

[ADR-0009](./0009-adapter-feature-parity.md) keeps every adapter feature-complete. This wait is not an ILD capability that adapters implement: it is behaviour of the agent CLI. The other adapters (Codex, OpenCode, Pi, GitHub Copilot) get no wait, which is what they had, since the removed wait only ever ran for Claude Code, and nothing is written for a CLI that has none.

## Consequences

- `/api/v1` loses the endpoints that restarted and resumed a waiting node, the run payload field that described the wait, and the two settings that bounded it. Their only callers were the bundled frontend and MCP server.
- The Claude stream reader has to treat several `result` events as one launch.
