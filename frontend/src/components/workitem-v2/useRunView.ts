import { useCallback, useEffect, useRef, useState } from "react";
import type { LoopRun, RunConversationMessage } from "../../types";
import type { TypedSignalRMessage } from "../../types/signalr";
import { loopRunService } from "../../services/auth";
import { useSignalR } from "../../hooks/useSignalR";

export interface RunView {
  /** The run's detail as last read; null until a read has landed. */
  run: LoopRun | null;
  /** The run's conversation so far, in event order, each message once. */
  messages: RunConversationMessage[];
  /** Why the last conversation read failed; null once one succeeds. */
  error: string | null;
}

function merge(
  known: RunConversationMessage[],
  read: RunConversationMessage[],
): RunConversationMessage[] {
  if (read.length === 0) return known;
  const byId = new Map(known.map((m) => [m.id, m]));
  for (const m of read) byId.set(m.id, m);
  return [...byId.values()].sort((a, b) => a.id - b.id);
}

/**
 * One run as the Action tab shows it: its detail and its conversation, kept
 * current over the run hub in whatever state the run is. Owned by a component
 * keyed by `runId`, so everything here belongs to that one run and goes with
 * it. The detail is read again when `actionsSettled`, the count of actions on
 * the run that went through, moves.
 */
export function useRunView(runId: string | null, actionsSettled: number): RunView {
  const [run, setRun] = useState<LoopRun | null>(null);
  const [messages, setMessages] = useState<RunConversationMessage[]>([]);
  const [error, setError] = useState<string | null>(null);
  const mounted = useRef(true);
  // The newest event a conversation read has covered; reads go on from it.
  const cursor = useRef(0);
  const reading = useRef(false);
  const readAgain = useRef(false);
  const runReads = useRef(0);
  const { on, off, invoke, connectionState } = useSignalR("/hubs/loop-run");

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  // One read at a time: asking during a read reads once more after it, from
  // wherever that read left the cursor.
  const readConversation = useCallback(
    function read() {
      if (!runId) return;
      if (reading.current) {
        readAgain.current = true;
        return;
      }
      reading.current = true;
      loopRunService
        .getConversation(runId, cursor.current)
        .then(
          (page) => {
            if (!mounted.current) return;
            cursor.current = Math.max(cursor.current, page.lastEventId ?? 0);
            setMessages((known) => merge(known, page.messages));
            setError(null);
          },
          (e: unknown) => {
            if (!mounted.current) return;
            setError(
              (e as { message?: string })?.message ?? "The conversation could not be loaded.",
            );
          },
        )
        .finally(() => {
          reading.current = false;
          if (mounted.current && readAgain.current) {
            readAgain.current = false;
            read();
          }
        });
    },
    [runId],
  );

  // The newest read wins; a failed one leaves the last detail in place.
  const readRun = useCallback(() => {
    if (!runId) return;
    const generation = ++runReads.current;
    loopRunService.getById(runId).then(
      (r) => {
        if (mounted.current && generation === runReads.current) setRun(r);
      },
      () => {},
    );
  }, [runId]);

  useEffect(() => {
    readConversation();
  }, [readConversation]);

  useEffect(() => {
    readRun();
  }, [readRun, actionsSettled]);

  // Events are hints: anything missed while disconnected is read again once
  // the connection is back and the run's group joined.
  useEffect(() => {
    if (!runId || connectionState !== "connected") return;
    let attached = true;
    const onEventLogged = (message: TypedSignalRMessage<"EventLogged">) => {
      if (message.payload.runId === runId && message.payload.id > cursor.current)
        readConversation();
    };
    const onRunChanged = (
      message: TypedSignalRMessage<"LoopRunStateChanged" | "NodeStateChanged" | "RunHalted">,
    ) => {
      if (message.payload.runId === runId) readRun();
    };
    on("EventLogged", onEventLogged);
    on("LoopRunStateChanged", onRunChanged);
    on("NodeStateChanged", onRunChanged);
    on("RunHalted", onRunChanged);
    void Promise.resolve(invoke("SubscribeToRun", runId))
      .catch(() => {})
      .then(() => {
        if (!attached) return;
        readConversation();
        readRun();
      });
    return () => {
      attached = false;
      off("EventLogged", onEventLogged);
      off("LoopRunStateChanged", onRunChanged);
      off("NodeStateChanged", onRunChanged);
      off("RunHalted", onRunChanged);
    };
  }, [runId, connectionState, on, off, invoke, readConversation, readRun]);

  return { run, messages, error };
}
