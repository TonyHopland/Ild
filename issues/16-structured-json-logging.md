## Parent

PRD.md

## Status

**COMPLETE**

## What to build

Switch Serilog to structured JSON console output and add runtime log level control. Logs are emitted as parseable JSON objects. An API endpoint and Settings UI control allow changing the log level at runtime without restarting the container.

Git command output and LLM API call details (latency, token usage) are included in the structured logs.

## Acceptance criteria

- [x] Serilog console output uses `WriteConsoleJson()` or equivalent JSON formatter
- [x] Log entries contain: timestamp, level, message, properties (source context, node type, run ID, etc.)
- [x] API endpoint `PUT /api/v1/logging/level` accepts log level change (Debug, Information, Warning, Error)
- [x] Settings page has a log level dropdown that calls the API
- [x] Log level change takes effect immediately without restart
- [x] Git command output is logged at Debug level with worktree and command details
- [ ] LLM API calls log latency and token usage at Debug level (deferred — adapter not yet built, issue 20)
- [ ] Tool invocations are logged at Debug level with tool name and result summary (deferred — adapter not yet built, issue 20)
- [x] Backend tests cover: JSON log format is valid JSON, log level change applies immediately
- [x] Frontend tests cover: log level dropdown renders and calls API on change
- [x] `vp check` and `vp test` pass

## Blocked by

None - can start immediately
