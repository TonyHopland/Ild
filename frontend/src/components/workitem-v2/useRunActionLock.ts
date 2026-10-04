import { useCallback, useMemo, useRef, useState } from "react";

export type RunAction =
  | "pause"
  | "resume"
  | "cancel"
  | "delete"
  | "retain"
  | "retry"
  | "reclaim"
  | "halt"
  | "steer"
  | "abandon";

export interface RunActionLock {
  /** The action in flight on a run, if any. */
  pendingOf: (runId: string | null | undefined) => RunAction | null;
  /**
   * How many actions on a run went through. A view of the run reads it again
   * when this moves, whichever control the action came from and whether or not
   * that view existed when it started.
   */
  settledOf: (runId: string | null | undefined) => number;
  /**
   * Runs `act` holding the run's one action slot until it settles, rethrowing
   * its failure; once `act` went through, counts it in `settledOf`. While another action holds that run's slot it refuses:
   * resolves false without running `act`. With no run id there is nothing to
   * hold, and `act` simply runs.
   */
  hold: (runId: string | null | undefined, kind: RunAction, act: () => unknown) => Promise<boolean>;
}

/**
 * One action at a time per run, whichever control started it: a retry, delete
 * or clean-up must never overlap another action on the same run. Owned by the
 * work item dialog, keyed by run id, so every entry point to a run's actions
 * shares it.
 */
export function useRunActionLock(): RunActionLock {
  const [pending, setPending] = useState<ReadonlyMap<string, RunAction>>(new Map());
  const [settled, setSettled] = useState<ReadonlyMap<string, number>>(new Map());
  // The synchronous truth `hold` decides on; `pending` is its rendered copy.
  const held = useRef(new Map<string, RunAction>());

  const hold = useCallback<RunActionLock["hold"]>(async (runId, kind, act) => {
    if (!runId) {
      await act();
      return true;
    }
    if (held.current.has(runId)) return false;
    held.current.set(runId, kind);
    setPending(new Map(held.current));
    let wentThrough = false;
    try {
      await act();
      wentThrough = true;
      return true;
    } finally {
      held.current.delete(runId);
      setPending(new Map(held.current));
      if (wentThrough) setSettled((prev) => new Map(prev).set(runId, (prev.get(runId) ?? 0) + 1));
    }
  }, []);

  const pendingOf = useCallback<RunActionLock["pendingOf"]>(
    (runId) => (runId ? (pending.get(runId) ?? null) : null),
    [pending],
  );

  const settledOf = useCallback<RunActionLock["settledOf"]>(
    (runId) => (runId ? (settled.get(runId) ?? 0) : 0),
    [settled],
  );

  return useMemo(() => ({ pendingOf, settledOf, hold }), [pendingOf, settledOf, hold]);
}
