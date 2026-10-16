import { useCallback, useEffect, useRef, useState } from "react";
import { useSignalR, type SignalRConnection } from "./useSignalR";
import { loopTemplateProposalService } from "../services/auth";

const NONE: Record<string, number> = {};

/**
 * How many loop template proposals wait for a human, per template id, read on
 * every (re)connect once the hub group is joined and on every proposal hint.
 * Only the latest read counts; one that fails shows no counts rather than old ones.
 * `hub` is a work-item hub connection the caller already holds; without it the
 * hook opens its own.
 */
export function usePendingLoopTemplateProposalCounts(
  hub?: SignalRConnection,
): Record<string, number> {
  const own = useSignalR(hub ? null : "/hubs/work-item");
  const { connectionState, on, off, invoke } = hub ?? own;
  // Read through a ref: joining again is for a (re)connect, not a new function.
  const invokeRef = useRef(invoke);
  invokeRef.current = invoke;
  const [counts, setCounts] = useState(NONE);
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
      let next = NONE;
      try {
        next = (await loopTemplateProposalService.pendingCounts()) ?? NONE;
      } catch (err) {
        console.error(err);
      }
      if (seq === seqRef.current) setCounts(next);
    })();
  }, []);

  useEffect(() => {
    if (connectionState !== "connected") return;
    let cancelled = false;
    void (async () => {
      try {
        await invokeRef.current("SubscribeToWorkItems");
      } catch (err) {
        console.error(err);
        return;
      }
      if (!cancelled) refresh();
    })();
    return () => {
      cancelled = true;
    };
  }, [connectionState, refresh]);

  useEffect(() => {
    on("LoopTemplateProposalsChanged", refresh);
    return () => off("LoopTemplateProposalsChanged", refresh);
  }, [on, off, refresh]);

  return counts;
}
