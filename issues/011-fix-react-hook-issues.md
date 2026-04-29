## What to build

Multiple React hook issues cause stale state, memory leaks, and incorrect behavior:

**A — `useSignalR` doesn't reconnect on auth change:** The dependency array only includes `hubUrl`. If the user logs in after mount, the hook won't establish a connection because `authService.getToken()` returns null at mount time.

**B — Duplicate SignalR handlers on re-render:** Every call to `on()` while connected registers a new `connection.on()` handler. The `handlersRef` deduplicates internally, but the SignalR connection accumulates handlers.

**C — `accessTokenFactory` captures stale token:** The token is captured at connection build time. If the token changes, the connection uses the stale value.

**D — Stale closure in Settings log level revert:** `setLogLevel(logLevel)` in a catch block uses the stale closure value, not the previous state.

**E — Missing cleanup for SignalR handlers in Taskboard:** Handlers registered in `useEffect` are not removed on unmount.

## Acceptance criteria

- [ ] `useSignalR` hook reconnects when auth token changes (add token to dependency array or use auth state subscription)
- [ ] `on()` calls deduplicate at the SignalR connection level (remove old handlers before adding new ones)
- [ ] `accessTokenFactory` reads token dynamically from `authService.getToken()`
- [ ] Settings log level revert uses functional state update or a ref for the previous value
- [ ] Taskboard SignalR handlers are cleaned up on component unmount
- [ ] `useEffect` in WorkItemModal depends on `workItem?.id` not the object reference

## Blocked by

None - can start immediately
