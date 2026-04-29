## What to build

`CmdNodeExecutor` stdout/stderr reads have no timeout. `proc.StandardOutput.ReadToEndAsync()` and `proc.StandardError.ReadToEndAsync()` have no timeout. If the process exits but the pipe doesn't close (a known edge case with redirected streams), these reads hang indefinitely.

Add a `CancellationToken` to the stream reads tied to the same timeout as the process execution.

**Decision logged:** No command injection restrictions. Cmd nodes are user-configured and the user is trusted to know what they're running. The container is the sandbox per the PRD.

## Acceptance criteria

- [ ] `ReadToEndAsync` on stdout/stderr uses a `CancellationToken` tied to the node timeout
- [ ] Normal command execution is unaffected
- [ ] Add test for stream read timeout behavior

## Blocked by

None - can start immediately
