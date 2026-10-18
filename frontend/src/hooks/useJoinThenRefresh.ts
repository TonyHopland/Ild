import { useEffect } from "react";
import { useLatest } from "./useLatest";
import type { SignalRConnection } from "./useSignalR";

/**
 * Joins a hub group on every (re)connect of `hub` and then calls `refresh`, since
 * a hint sent while disconnected is lost. The group is the chat `chatSessionId`
 * on the chat hub, left again on cleanup, or the board on the work-item hub when
 * it is null. A join that fails reads nothing. Joining again follows a
 * (re)connect or a new group, never a new `invoke` function.
 */
export function useJoinThenRefresh(
  hub: SignalRConnection,
  chatSessionId: string | null,
  refresh: () => void,
) {
  const { connectionState } = hub;
  const invokeRef = useLatest(hub.invoke);

  useEffect(() => {
    if (connectionState !== "connected") return;
    const invoke = invokeRef.current;
    let cancelled = false;
    void (async () => {
      try {
        // A read before the join has taken effect could miss a hint sent in the gap.
        await (chatSessionId !== null
          ? invoke("SubscribeToChat", chatSessionId)
          : invoke("SubscribeToWorkItems"));
      } catch (err) {
        console.error(err);
        return;
      }
      if (!cancelled) refresh();
    })();
    return () => {
      cancelled = true;
      if (chatSessionId !== null) {
        void invoke("UnsubscribeFromChat", chatSessionId)?.catch((err) => console.error(err));
      }
    };
  }, [connectionState, invokeRef, chatSessionId, refresh]);
}
