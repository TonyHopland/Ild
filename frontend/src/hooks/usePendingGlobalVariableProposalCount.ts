import { useCallback, useEffect, useRef, useState } from "react";
import { useSignalR } from "./useSignalR";
import { globalVariableService } from "../services/auth";

/**
 * How many global variable suggestions wait for a human, read on every
 * (re)connect once the hub group is joined and on every suggestion hint. Only
 * the latest read counts; one that fails shows no count rather than an old one.
 */
export function usePendingGlobalVariableProposalCount(): number {
  const { connectionState, on, off, invoke } = useSignalR("/hubs/work-item");
  const [count, setCount] = useState(0);
  const seqRef = useRef(0);

  useEffect(
    () => () => {
      seqRef.current++;
    },
    [],
  );

  const refresh = useCallback(() => {
    const seq = ++seqRef.current;
    void (async () => {
      let next = 0;
      try {
        next = await globalVariableService.pendingProposalCount();
      } catch (err) {
        console.error(err);
      }
      if (seq === seqRef.current) setCount(next);
    })();
  }, []);

  useEffect(() => {
    if (connectionState !== "connected") return;
    let cancelled = false;
    void (async () => {
      try {
        await invoke("SubscribeToWorkItems");
      } catch (err) {
        console.error(err);
        return;
      }
      if (!cancelled) refresh();
    })();
    return () => {
      cancelled = true;
    };
  }, [connectionState, invoke, refresh]);

  useEffect(() => {
    on("GlobalVariableProposalsChanged", refresh);
    return () => off("GlobalVariableProposalsChanged", refresh);
  }, [on, off, refresh]);

  return count;
}
