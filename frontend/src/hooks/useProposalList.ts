import { useCallback, useEffect, useRef, useState } from "react";
import { useSignalR, type SignalRConnection } from "./useSignalR";
import type { SignalREventName, SignalREventPayloads, TypedSignalRMessage } from "../types/signalr";

/** A hub event that says a target's proposals changed, and which of its payloads concern the target. */
export interface ProposalHint {
  event: SignalREventName;
  matches: (payload: unknown) => boolean;
}

export function proposalHint<E extends SignalREventName>(
  event: E,
  matches: (payload: SignalREventPayloads[E]) => boolean = () => true,
): ProposalHint {
  return { event, matches: matches as (payload: unknown) => boolean };
}

/**
 * One kind of proposal as its list is read. A target is a mode and an id; the
 * "chat" mode is a chat, hinted on the chat hub to the chat's group, and every
 * other mode is hinted on the work-item hub to the board.
 */
export interface ProposalListKind<M extends string, T> {
  read: (mode: M, id: string) => Promise<T[]>;
  hint: (mode: M, id: string) => ProposalHint;
}

interface ProposalsView<T> {
  targetKey: string;
  proposals: T[];
}

/**
 * The proposals of one kind for one target, kept current by snapshot reads: on
 * every (re)connect once the hub group is joined, on a hint for this target, and
 * on `refresh`. Only the latest read for the current target is shown. `hub` is a
 * connection to the hub the target is hinted on that the caller already holds;
 * without it the hook opens its own.
 */
export function useProposalList<M extends string, T>(
  kind: ProposalListKind<M, T>,
  mode: M,
  id: string,
  hub?: SignalRConnection,
) {
  const targetKey = `${mode}:${id}`;
  const chat = mode === "chat";
  const own = useSignalR(hub ? null : chat ? "/hubs/chat" : "/hubs/work-item");
  const { connectionState, on, off, invoke } = hub ?? own;

  // Read through a ref: joining again is for a (re)connect or a new target, not a new function.
  const invokeRef = useRef(invoke);
  invokeRef.current = invoke;

  const [view, setView] = useState<ProposalsView<T> | null>(null);
  const generationRef = useRef(0);
  const currentTargetRef = useRef<string | null>(null);

  useEffect(() => {
    currentTargetRef.current = targetKey;
    return () => {
      currentTargetRef.current = null;
      generationRef.current++;
    };
  }, [targetKey]);

  const refresh = useCallback(() => {
    if (currentTargetRef.current !== targetKey) return;
    const generation = ++generationRef.current;
    void (async () => {
      try {
        const proposals = await kind.read(mode, id);
        if (generation !== generationRef.current || currentTargetRef.current !== targetKey) return;
        setView({ targetKey, proposals });
      } catch (err) {
        console.error(err);
      }
    })();
  }, [kind, targetKey, mode, id]);

  useEffect(() => {
    if (connectionState !== "connected") return;
    let cancelled = false;
    void (async () => {
      try {
        // A read before the join has taken effect could miss a hint sent in the gap.
        await (chat
          ? invokeRef.current("SubscribeToChat", id)
          : invokeRef.current("SubscribeToWorkItems"));
      } catch (err) {
        console.error(err);
        return;
      }
      if (!cancelled) refresh();
    })();
    return () => {
      cancelled = true;
      if (chat) {
        void invokeRef.current("UnsubscribeFromChat", id)?.catch((err) => console.error(err));
      }
    };
  }, [connectionState, chat, id, refresh]);

  useEffect(() => {
    const hint = kind.hint(mode, id);
    const onChanged = (msg: TypedSignalRMessage) => {
      if (hint.matches(msg.payload)) refresh();
    };
    on(hint.event, onChanged);
    return () => off(hint.event, onChanged);
  }, [on, off, kind, mode, id, refresh]);

  const proposals = view?.targetKey === targetKey ? view.proposals : undefined;
  return { proposals, refresh };
}
