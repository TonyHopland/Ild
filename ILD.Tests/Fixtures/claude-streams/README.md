# Claude Code streams with background work

Standard output of real `claude --print --output-format stream-json --verbose` launches, one event per line, captured to show
what the CLI does when a turn ends with work still running. They are what the Claude stream reader is tested against.

## How they were captured

- Claude Code 2.1.326, one launch per file, the prompt as the last argument after `--`.
- Standard input was closed (`< /dev/null`), except in `registered-background-command-stdin-open`, where it was a pipe that
  stayed open for the whole launch and never carried data.
- Each launch ran with only the commands it needed allowed (`--allowedTools`), not with `--permission-mode bypassPermissions`,
  and on a small model. Neither changes the events for background work.
- Captured on Windows. Paths were rewritten to `/tmp/claude/session/...`, and the `init` event was emptied of the MCP servers,
  plugins, skills, agents and commands of the machine. Nothing else was changed: ids, order and every other field are the CLI's.

The seconds below are counted from the first line of the stream.

## What each stream shows

### `registered-background-command.jsonl`
A Bash call with `run_in_background: true` (`sleep 20`), then the turn ends.

    + 0s system/init
    + 1s system/background_tasks_changed   tasks: [local_bash]
    + 1s system/task_started               is_backgrounded: true
    + 2s result  result_index 0            "STARTED"
    +21s system/task_updated               patch.status: completed
    +21s system/task_notification          status: completed, summary carries the exit code
    +21s system/background_tasks_changed   tasks: []
    +21s system/init
    +22s result  result_index 1            "FINISHED", origin.kind: task-notification

The process stayed open for the command and exited after the second `result`.

### `registered-background-command-stdin-open.jsonl`
The same with standard input left open. The stream is the same: the wait does not depend on standard input being closed.
The CLI writes one warning to standard error after 3 seconds ("no stdin data received in 3s, proceeding without it").

### `unregistered-leftover-process.jsonl`
A foreground Bash call that starts a process with `nohup ... &` and returns. One `result`, no task event of any kind, and the
process exited within a second of it while the leftover was still running.

### `registered-command-and-unregistered-leftover.jsonl`
Both in one turn. The stream is that of the registered command alone: the leftover appears nowhere and is not waited for.

### `background-subagent.jsonl`
An Agent call with `run_in_background: true` whose subagent runs `sleep 15` in the foreground.

    + 0s system/init
    + 1s system/background_tasks_changed   tasks: [local_agent]
    + 1s system/task_started               the subagent, is_backgrounded: true
    + 5s system/task_started               the subagent's own command, is_backgrounded: false, parent_task_id set
    +17s system/task_notification          the subagent's command
    +18s system/task_updated               the subagent
    +18s system/task_notification          the subagent
    +18s system/background_tasks_changed   tasks: []
    +18s system/init
    +19s result  result_index 0            "STARTED"
    +19s result  result_index 1            "FINISHED", origin.kind: task-notification

Unlike a background command, the first turn's `result` is held back until the subagent has finished. The subagent's own
foreground command gets `task_started` and `task_notification` but never appears in `background_tasks_changed`.

## What holds across all of them

- A launch has one `result` per turn, numbered by `result_index`. A turn the CLI started itself carries
  `origin.kind: "task-notification"`; the first has no `origin`.
- `result` (the text), `num_turns` and `usage` are of that turn only. `total_cost_usd` is the launch's running total, so the
  last `result` holds the cost of the whole launch; `model_usage_since_last_result` is the turn's share.
- `background_tasks_changed` always carries the whole live set (`task_id`, `task_type`, `description`, and `subagent_type`
  for an agent) and is sent when it changes, including the empty set at the end.
- A second `system/init` precedes each follow-up turn.
- The process exits after the last `result`.
