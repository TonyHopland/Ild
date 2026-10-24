import { useCallback, useEffect, useRef, useState } from "react";
import { useJoinThenRefresh } from "./useJoinThenRefresh";
import { useLatest } from "./useLatest";
import { useSharedWorkItemHub } from "./useWorkItemHub";
import type { SignalREventName } from "../types/signalr";

/**
 * One kind of proposal as its pending count is read. `key` names the kind: a
 * count keeps its subscription and read while the key stays the same, however
 * often the descriptor itself is rebuilt. `empty` is shown before the first read
 * and after a read that fails.
 */
export interface ProposalCountKind<T> {
  key: string;
  read: () => Promise<T>;
  empty: T;
  hint: SignalREventName;
}

interface CountView<T> {
  key: string;
  count: T;
}

/** The kind a count reads for, while it is still the count's kind and mounted. */
interface Reader {
  key: string;
  live: boolean;
}

/**
 * How many proposals of one kind wait for a human, read on every (re)connect
 * once the board is joined and on every hint. Only the latest read counts; one
 * that fails shows the kind's empty value rather than an old count. It listens
 * on the app's shared work-item hub.
 */
export function usePendingProposalCount<T>(kind: ProposalCountKind<T>): T {
  const { key, hint } = kind;
  const kindRef = useLatest(kind);
  const connection = useSharedWorkItemHub();
  const { on, off } = connection;
  const [view, setView] = useState<CountView<T> | null>(null);
  const seqRef = useRef(0);
  const readerRef = useRef<Reader | null>(null);

  useEffect(() => {
    const reader = { key, live: true };
    readerRef.current = reader;
    return () => {
      reader.live = false;
    };
  }, [key]);

  const refresh = useCallback(() => {
    const reader = readerRef.current;
    if (reader === null || reader.key !== key || !reader.live) return;
    const seq = ++seqRef.current;
    const { read, empty } = kindRef.current;
    void (async () => {
      let count = empty;
      try {
        count = await read();
      } catch (err) {
        console.error(err);
      }
      if (seq === seqRef.current && reader.live) setView({ key, count });
    })();
  }, [kindRef, key]);

  useJoinThenRefresh(connection, null, refresh);

  useEffect(() => {
    on(hint, refresh);
    return () => off(hint, refresh);
  }, [on, off, hint, refresh]);

  return view?.key === key ? view.count : kind.empty;
}
