import { useCallback, useEffect, useRef, useState } from "react";
import { useSignalR } from "./useSignalR";
import { workItemService } from "../services/auth";
import type { WorkItemEditProposal } from "../types";
import type { TypedSignalRMessage } from "../types/signalr";

export type EditProposalsTarget =
  | { workItemId: string }
  | { requestedByWorkItemId: string }
  | { chatSessionId: string };

interface ProposalsView {
  targetKey: string;
  proposals: WorkItemEditProposal[];
}

function modeOf(target: EditProposalsTarget) {
  if ("workItemId" in target) return ["item", target.workItemId] as const;
  if ("requestedByWorkItemId" in target)
    return ["requester", target.requestedByWorkItemId] as const;
  return ["chat", target.chatSessionId] as const;
}

function read(mode: "item" | "requester" | "chat", id: string) {
  if (mode === "item") return workItemService.listEditProposals(id);
  if (mode === "requester") return workItemService.listRequestedEditProposals(id);
  return workItemService.listEditProposalsFor({ chatSessionId: id });
}

/**
 * The edit proposals for one work item, those its loop runs requested on any
 * item, or those made by one chat, kept current by snapshot reads: on every
 * (re)connect once the hub group is joined, on a hint for this target, and on
 * {@link refresh}. A requesting item is hinted as a work item.
 */
export function useEditProposals(target: EditProposalsTarget) {
  const [mode, id] = modeOf(target);
  const targetKey = `${mode}:${id}`;
  const { connectionState, on, off, invoke } = useSignalR(
    mode === "chat" ? "/hubs/chat" : "/hubs/work-item",
  );

  const [view, setView] = useState<ProposalsView | null>(null);
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
        const proposals = await read(mode, id);
        if (generation !== generationRef.current || currentTargetRef.current !== targetKey) return;
        setView({ targetKey, proposals });
      } catch (err) {
        console.error(err);
      }
    })();
  }, [targetKey, mode, id]);

  useEffect(() => {
    if (connectionState !== "connected") return;
    let cancelled = false;
    void (async () => {
      try {
        // A read before the join has taken effect could miss a hint sent in the gap.
        await (mode === "chat" ? invoke("SubscribeToChat", id) : invoke("SubscribeToWorkItems"));
      } catch (err) {
        console.error(err);
        return;
      }
      if (!cancelled) refresh();
    })();
    return () => {
      cancelled = true;
      if (mode === "chat") {
        void invoke("UnsubscribeFromChat", id)?.catch((err) => console.error(err));
      }
    };
  }, [connectionState, mode, id, invoke, refresh]);

  useEffect(() => {
    if (mode !== "chat") {
      const onChanged = (msg: TypedSignalRMessage<"WorkItemEditProposalsChanged">) => {
        if (msg.payload.workItemId === id) refresh();
      };
      on("WorkItemEditProposalsChanged", onChanged);
      return () => off("WorkItemEditProposalsChanged", onChanged);
    }
    const onChanged = (msg: TypedSignalRMessage<"ChatEditProposalsChanged">) => {
      if (msg.payload.chatSessionId === id) refresh();
    };
    on("ChatEditProposalsChanged", onChanged);
    return () => off("ChatEditProposalsChanged", onChanged);
  }, [on, off, mode, id, refresh]);

  const proposals = view?.targetKey === targetKey ? view.proposals : undefined;
  return { proposals, refresh };
}
