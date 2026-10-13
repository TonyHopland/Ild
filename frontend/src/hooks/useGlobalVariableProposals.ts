import { useCallback, useEffect, useRef, useState } from "react";
import { useSignalR } from "./useSignalR";
import { globalVariableService } from "../services/auth";
import type { GlobalVariableProposal } from "../types";
import type { TypedSignalRMessage } from "../types/signalr";

export type GlobalVariableProposalsTarget = { workItemId: string } | { chatSessionId: string };

interface ProposalsView {
  targetKey: string;
  proposals: GlobalVariableProposal[];
}

function modeOf(target: GlobalVariableProposalsTarget) {
  if ("workItemId" in target) return ["item", target.workItemId] as const;
  return ["chat", target.chatSessionId] as const;
}

/**
 * The global variable suggestions one work item's loop runs made, or those one
 * chat made, kept current by snapshot reads: on every (re)connect once the hub
 * group is joined, on a hint for this target, and on {@link refresh}. Any
 * suggestion hint reaches a work item, since it does not say which run made it.
 */
export function useGlobalVariableProposals(target: GlobalVariableProposalsTarget) {
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
        const proposals = await globalVariableService.listProposals(
          mode === "item" ? { workItemId: id } : { chatSessionId: id },
        );
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
      const onChanged = () => refresh();
      on("GlobalVariableProposalsChanged", onChanged);
      return () => off("GlobalVariableProposalsChanged", onChanged);
    }
    const onChanged = (msg: TypedSignalRMessage<"ChatGlobalVariableProposalsChanged">) => {
      if (msg.payload.chatSessionId === id) refresh();
    };
    on("ChatGlobalVariableProposalsChanged", onChanged);
    return () => off("ChatGlobalVariableProposalsChanged", onChanged);
  }, [on, off, mode, id, refresh]);

  const proposals = view?.targetKey === targetKey ? view.proposals : undefined;
  return { proposals, refresh };
}
