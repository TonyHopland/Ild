/**
 * Decide what (if anything) to show the user when a terminal WebSocket closes.
 *
 * The raw close code is deliberately never exposed: `1006` ("abnormal closure")
 * and friends describe *how* the socket closed — an idle/proxy timeout, a
 * network blip, the server dropping the socket — not *whether* the work
 * succeeded. Surfacing it reads as a scary error even when everything is fine.
 *
 * @param code      The WebSocket close code from the `close` event.
 * @param hadOpened Whether this socket ever reached the "open" state.
 * @param errorHint Optional caller-provided hint for a genuine connect failure.
 * @param reason    The close frame's reason, when the server gave one.
 * @returns A user-facing message, or `null` to show nothing.
 */
export function describeTerminalClose(
  code: number,
  hadOpened: boolean,
  errorHint?: string,
  reason?: string,
): string | null {
  // 1000 is a clean close: a normal session end or the client tearing the
  // socket down itself. Nothing went wrong, so say nothing.
  if (code === 1000) return null;
  // The server said why it closed the terminal (a run still executing, a worktree
  // that is gone, a shell that would not start): that beats any guess.
  if (reason?.trim()) return reason.trim();
  // The socket worked and then dropped — the connection was lost mid-session.
  // Give a plain, actionable message rather than a cryptic close code.
  if (hadOpened) return "Connection lost. Close and reopen the terminal to reconnect.";
  // The socket never opened: this is a genuine failure to connect.
  return errorHint ?? "Unable to connect.";
}
