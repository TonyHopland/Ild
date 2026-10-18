import { useEffect, useRef } from "react";
import type { SignalRConnection } from "./useSignalR";

/**
 * Joins a hub group on every (re)connect of `hub` and then calls `refresh`, since
 * a hint sent while disconnected is lost. The group is the chat `chatSessionId`
 * on the chat hub, left again on cleanup, or the board on the work-item hub when
 * it is null. A join that fails reads nothing.
 */
export function useJoinThenRefresh(
  hub: SignalRConnection,
  chatSessionId: string | null,
  refresh: () => void,
) {
  const { connectionState, invoke } = hub;
  // Read through a ref: joining again is for a (re)connect or a new group, not a new function.
  const invokeRef = useRef(invoke);
  invokeRef.current = invoke;

  useEffect(() => {
    if (connectionState !== "connected") return;
    let cancelled = false;
    void (async () => {
      try {
        // A read before the join has taken effect could miss a hint sent in the gap.
        await (chatSessionId !== null
          ? invokeRef.current("SubscribeToChat", chatSessionId)
          : invokeRef.current("SubscribeToWorkItems"));
      } catch (err) {
        console.error(err);
        return;
      }
      if (!cancelled) refresh();
    })();
    return () => {
      cancelled = true;
      if (chatSessionId !== null) {
        void invokeRef
          .current("UnsubscribeFromChat", chatSessionId)
          ?.catch((err) => console.error(err));
      }
    };
  }, [connectionState, chatSessionId, refresh]);
}
