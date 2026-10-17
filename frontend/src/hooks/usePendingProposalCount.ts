import { useCallback, useEffect, useRef, useState } from "react";
import { useSignalR, type SignalRConnection } from "./useSignalR";
import type { SignalREventName } from "../types/signalr";

/**
 * How many proposals of one kind wait for a human, read on every (re)connect
 * once the board is joined and on every `hint`. Only the latest read counts; one
 * that fails shows `empty` rather than an old count. `hub` is a work-item hub
 * connection the caller already holds; without it the hook opens its own.
 */
export function usePendingProposalCount<T>(
  read: () => Promise<T>,
  hint: SignalREventName,
  empty: T,
  hub?: SignalRConnection,
): T {
  const own = useSignalR(hub ? null : "/hubs/work-item");
  const { connectionState, on, off, invoke } = hub ?? own;
  // Read through a ref: joining again is for a (re)connect, not a new function.
  const invokeRef = useRef(invoke);
  invokeRef.current = invoke;
  const [count, setCount] = useState(empty);
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
      let next = empty;
      try {
        next = await read();
      } catch (err) {
        console.error(err);
      }
      if (seq === seqRef.current) setCount(next);
    })();
  }, [read, empty]);

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
    on(hint, refresh);
    return () => off(hint, refresh);
  }, [on, off, hint, refresh]);

  return count;
}
