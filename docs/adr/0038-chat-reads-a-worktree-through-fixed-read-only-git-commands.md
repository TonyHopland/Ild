# ADR-0038: Chat reads a work item's worktree through fixed, read-only git commands

A chat could not see a work item's uncommitted changes. `clone_repository` only sees what was pushed, `pull_branch` moves commits and exposes no files, and the run's terminal is an interactive, user-only WebSocket PTY (`GET loop-runs/{id}/terminal`, `IRunHost.OpenTerminalAsync`) that an agent cannot reach and should not get.

## Decision

- **A new agent-API family, not the terminal.** `GET /api/v1/agent/workitems/{id}/worktree/{status,diff,file,search,log}` and `.../worktree/commits/{sha}`, with MCP tools `get_worktree_status`, `get_worktree_diff`, `read_worktree_file`, `search_worktree`, `get_worktree_log` and `get_worktree_commit`. They answer `{text, truncated, omittedBytes}`.
- **Through the run host, never the server's own filesystem.** Runs live in containers or pods, so ILD cannot assume it can open the worktree. Every git command goes through `RepositoryManager`, which runs it agent-scoped through `IRunHost.RunToolAsync`: an argument list, no shell, and only in a directory `IsRunCloneAsync` vouches for. The one file read uses the same link-resolving boundary as the Files tab.
- **A fixed allowlist.** `IWorktreeInspector` builds each argument list itself: `status`, `diff`, `log`, `show`, `grep`, plus `ls-files`, `rev-parse` and `merge-base` for lookups. Caller input only ever fills a path slot after `--`, the pattern slot after `-e`, a number, or a lowercase hex sha that has been resolved and checked to be in the branch's history. Git runs with `--no-optional-locks`, `--literal-pathspecs`, `core.fsmonitor=false`, no pager, and `--no-ext-diff --no-textconv` for diffs.
- **Paths stay inside the worktree.** Absolute paths, `..`, NUL, backslashes and any `.git` segment are refused (400). A file read resolves links first and refuses anything that lands outside the worktree or inside `.git` (404).
- **Output is capped** at 128 KiB and 2000 lines. Git's output past the byte cap is counted, not held, and the text ends in `[truncated: N more bytes not shown]`. A binary file is reported by size.
- **Scope.** The route takes a work item id and reads only that item's worktree. A run reaches only its own work item and the items it created, as for the other worktree routes; a chat, driven by the operator, reaches any work item it names, which is how chats already reach worktrees. **An external-agent grant is refused outright** ([ADR-0037](0037-external-agents-use-expiring-revocable-grants.md)): a grant reaches the items it created, not their code.
- **No credentials.** The commands run as the agent, with the agent's environment, so they hold nothing the agent does not, and reach no network.

## Considered options

- **Reuse the terminal.** Rejected: it is interactive, user-only and an arbitrary shell.
- **Read the worktree on the ILD server.** Rejected: with container and pod hosts the path is not on its disk.
- **Mount the worktree read-only into the chat.** Rejected: it needs a per-host mount for every run host, and gives chat arbitrary reads of `.git`.
- **Free-form `git` for chat.** Rejected: options such as `--output` write files.

## Consequences

- Chat can review a running work item before anything is pushed. Untracked files appear in the status and per-path diff, not in the whole diff.
- A new read-only git verb needs a change to the inspector, not a setting.
