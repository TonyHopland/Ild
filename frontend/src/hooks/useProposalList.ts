import { useCallback, useEffect, useRef, useState } from "react";
import { useJoinThenRefresh } from "./useJoinThenRefresh";
import { useLatest } from "./useLatest";
import { useSignalR, type SignalRConnection } from "./useSignalR";
import type { SignalREventName, SignalREventPayloads, TypedSignalRMessage } from "../types/signalr";

/**
 * Where a target's proposals are hinted to have changed: to one chat's group on
 * the chat hub, or to the board on the work-item hub when `chatSessionId` is
 * null. `listen` calls `changed` on each hint about the target, and returns how
 * to stop.
 */
export interface ProposalHint {
  chatSessionId: string | null;
  listen: (hub: Pick<SignalRConnection, "on" | "off">, changed: () => void) => () => void;
}

/** A hint sent to the board on the work-item hub. */
export function boardHint<E extends SignalREventName>(
  event: E,
  matches: (payload: TypedSignalRMessage<E>["payload"]) => boolean = () => true,
): ProposalHint {
  return {
    chatSessionId: null,
    listen: (hub, changed) => {
      const onHint = (msg: TypedSignalRMessage<E>) => {
        if (matches(msg.payload)) changed();
      };
      hub.on(event, onHint);
      return () => hub.off(event, onHint);
    },
  };
}

type ChatHintEvent = {
  [E in SignalREventName]: SignalREventPayloads[E] extends { chatSessionId: string } ? E : never;
}[SignalREventName];

/** A hint sent to the group of chat `chatSessionId` on the chat hub, about that chat. */
export function chatHint<E extends ChatHintEvent>(event: E, chatSessionId: string): ProposalHint {
  return {
    chatSessionId,
    listen: (hub, changed) => {
      const onHint = (msg: TypedSignalRMessage<E>) => {
        if (msg.payload.chatSessionId === chatSessionId) changed();
      };
      hub.on(event, onHint);
      return () => hub.off(event, onHint);
    },
  };
}

/**
 * One kind of proposal as its list is read: a target is a mode and an id.
 * `key` names the kind: a list keeps its subscription and read while the key
 * stays the same, however often the descriptor itself is rebuilt.
 */
export interface ProposalListKind<M extends string, T> {
  key: string;
  read: (mode: M, id: string) => Promise<T[]>;
  hint: (mode: M, id: string) => ProposalHint;
}

interface ProposalsView<T> {
  targetKey: string;
  proposals: T[];
}

/** The target a list reads for, while it is still the list's target. */
interface Target {
  key: string;
  live: boolean;
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
  const targetKey = `${kind.key}:${mode}:${id}`;
  const kindRef = useLatest(kind);
  const { chatSessionId } = kind.hint(mode, id);
  const own = useSignalR(hub ? null : chatSessionId !== null ? "/hubs/chat" : "/hubs/work-item");
  const connection = hub ?? own;
  const { on, off } = connection;

  const [view, setView] = useState<ProposalsView<T> | null>(null);
  const generationRef = useRef(0);
  const targetRef = useRef<Target | null>(null);

  useEffect(() => {
    const target = { key: targetKey, live: true };
    targetRef.current = target;
    return () => {
      target.live = false;
    };
  }, [targetKey]);

  const refresh = useCallback(() => {
    const target = targetRef.current;
    if (target === null || target.key !== targetKey || !target.live) return;
    const generation = ++generationRef.current;
    const { read } = kindRef.current;
    void (async () => {
      try {
        const proposals = await read(mode, id);
        if (generation !== generationRef.current || !target.live) return;
        setView({ targetKey, proposals });
      } catch (err) {
        console.error(err);
      }
    })();
  }, [kindRef, targetKey, mode, id]);

  useJoinThenRefresh(connection, chatSessionId, refresh);

  useEffect(
    () => kindRef.current.hint(mode, id).listen({ on, off }, refresh),
    [kindRef, on, off, mode, id, refresh],
  );

  const proposals = view?.targetKey === targetKey ? view.proposals : undefined;
  return { proposals, refresh };
}
