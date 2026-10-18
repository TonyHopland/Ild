import { useCallback, useEffect, useRef, useState } from "react";
import { useJoinThenRefresh } from "./useJoinThenRefresh";
import { useSignalR, type SignalRConnection } from "./useSignalR";
import type { SignalREventName, SignalREventPayloads, TypedSignalRMessage } from "../types/signalr";

/**
 * A hub event that says a target's proposals changed, which of its payloads
 * concern the target, and where it is sent: to one chat's group on the chat hub,
 * or to the board on the work-item hub when `chatSessionId` is null.
 */
export interface ProposalHint {
  event: SignalREventName;
  matches: (payload: unknown) => boolean;
  chatSessionId: string | null;
}

/** A hint sent to the board on the work-item hub. */
export function boardHint<E extends SignalREventName>(
  event: E,
  matches: (payload: SignalREventPayloads[E]) => boolean = () => true,
): ProposalHint {
  return { event, matches: matches as (payload: unknown) => boolean, chatSessionId: null };
}

type ChatHintEvent = {
  [E in SignalREventName]: SignalREventPayloads[E] extends { chatSessionId: string } ? E : never;
}[SignalREventName];

/** A hint sent to the group of chat `chatSessionId` on the chat hub, about that chat. */
export function chatHint(event: ChatHintEvent, chatSessionId: string): ProposalHint {
  return {
    event,
    matches: (payload) => (payload as { chatSessionId: string }).chatSessionId === chatSessionId,
    chatSessionId,
  };
}

/** One kind of proposal as its list is read: a target is a mode and an id. */
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
  // Read through a ref: a caller may pass the kind inline, new every render.
  const kindRef = useRef(kind);
  kindRef.current = kind;
  const { chatSessionId } = kind.hint(mode, id);
  const own = useSignalR(hub ? null : chatSessionId !== null ? "/hubs/chat" : "/hubs/work-item");
  const connection = hub ?? own;
  const { on, off } = connection;

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
        const proposals = await kindRef.current.read(mode, id);
        if (generation !== generationRef.current || currentTargetRef.current !== targetKey) return;
        setView({ targetKey, proposals });
      } catch (err) {
        console.error(err);
      }
    })();
  }, [targetKey, mode, id]);

  useJoinThenRefresh(connection, chatSessionId, refresh);

  useEffect(() => {
    const hint = kindRef.current.hint(mode, id);
    const onChanged = (msg: TypedSignalRMessage) => {
      if (hint.matches(msg.payload)) refresh();
    };
    on(hint.event, onChanged);
    return () => off(hint.event, onChanged);
  }, [on, off, mode, id, refresh]);

  const proposals = view?.targetKey === targetKey ? view.proposals : undefined;
  return { proposals, refresh };
}
