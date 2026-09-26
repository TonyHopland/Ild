import { useCallback, useEffect, useRef, useState } from "react";
import { useSignalR } from "./useSignalR";
import { workItemService } from "../services/auth";
import type { WorkItemEditProposal } from "../types";
import type { TypedSignalRMessage } from "../types/signalr";

export type EditProposalsTarget = { workItemId: string } | { chatSessionId: string };

interface ProposalsView {
  targetKey: string;
  proposals: WorkItemEditProposal[];
}

/**
 * The edit proposals for one work item, or those made by one chat, kept current
 * by snapshot reads: on every (re)connect once the hub group is joined, on a hint
 * for this target, and on {@link refresh}.
 */
export function useEditProposals(target: EditProposalsTarget) {
  const [mode, id] =
    "workItemId" in target
      ? (["item", target.workItemId] as const)
      : (["chat", target.chatSessionId] as const);
  const targetKey = `${mode}:${id}`;
  const { connectionState, on, off, invoke } = useSignalR(
    mode === "item" ? "/hubs/work-item" : "/hubs/chat",
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
        const proposals =
          mode === "item"
            ? await workItemService.listEditProposals(id)
            : await workItemService.listEditProposalsFor({ chatSessionId: id });
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
    if (mode === "item") {
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
