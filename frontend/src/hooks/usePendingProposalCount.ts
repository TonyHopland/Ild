import { useCallback, useEffect, useRef, useState } from "react";
import { useJoinThenRefresh } from "./useJoinThenRefresh";
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
  const connection = hub ?? own;
  const { on, off } = connection;
  // Read through refs: a caller may pass these inline, new every render.
  const readRef = useRef(read);
  readRef.current = read;
  const emptyRef = useRef(empty);
  emptyRef.current = empty;
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
      let next = emptyRef.current;
      try {
        next = await readRef.current();
      } catch (err) {
        console.error(err);
      }
      if (seq === seqRef.current) setCount(next);
    })();
  }, []);

  useJoinThenRefresh(connection, null, refresh);

  useEffect(() => {
    on(hint, refresh);
    return () => off(hint, refresh);
  }, [on, off, hint, refresh]);

  return count;
}
