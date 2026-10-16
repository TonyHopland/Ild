import { useCallback, useEffect, useRef, useState } from "react";
import { useSignalR } from "./useSignalR";
import { loopTemplateProposalService } from "../services/auth";
import type { LoopTemplateProposal } from "../types";
import type { TypedSignalRMessage } from "../types/signalr";

export type LoopTemplateProposalsTarget =
  | { workItemId: string }
  | { chatSessionId: string }
  | { templateId: string };

interface ProposalsView {
  targetKey: string;
  proposals: LoopTemplateProposal[];
}

function modeOf(target: LoopTemplateProposalsTarget) {
  if ("workItemId" in target) return ["item", target.workItemId] as const;
  if ("templateId" in target) return ["template", target.templateId] as const;
  return ["chat", target.chatSessionId] as const;
}

/**
 * The loop template proposals one work item's loop runs made, those one chat
 * made, or those pending for one template, kept current by snapshot reads: on
 * every (re)connect once the hub group is joined, on a hint for this target, and
 * on {@link refresh}. Any proposal hint reaches a work item, since it does not
 * say which run made it.
 */
export function useLoopTemplateProposals(target: LoopTemplateProposalsTarget) {
  const [mode, id] = modeOf(target);
  const targetKey = `${mode}:${id}`;
  const { connectionState, on, off, invoke } = useSignalR(
    mode === "chat" ? "/hubs/chat" : "/hubs/work-item",
  );

  // Read through a ref: joining again is for a (re)connect or a new target, not a new function.
  const invokeRef = useRef(invoke);
  invokeRef.current = invoke;

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
        const proposals = await loopTemplateProposalService.list(
          mode === "item"
            ? { workItemId: id }
            : mode === "template"
              ? { templateId: id, status: "Pending" }
              : { chatSessionId: id },
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
        await (mode === "chat"
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
      if (mode === "chat") {
        void invokeRef.current("UnsubscribeFromChat", id)?.catch((err) => console.error(err));
      }
    };
  }, [connectionState, mode, id, refresh]);

  useEffect(() => {
    if (mode !== "chat") {
      const onChanged = (msg: TypedSignalRMessage<"LoopTemplateProposalsChanged">) => {
        if (mode === "item" || msg.payload.templateId === id) refresh();
      };
      on("LoopTemplateProposalsChanged", onChanged);
      return () => off("LoopTemplateProposalsChanged", onChanged);
    }
    const onChanged = (msg: TypedSignalRMessage<"ChatLoopTemplateProposalsChanged">) => {
      if (msg.payload.chatSessionId === id) refresh();
    };
    on("ChatLoopTemplateProposalsChanged", onChanged);
    return () => off("ChatLoopTemplateProposalsChanged", onChanged);
  }, [on, off, mode, id, refresh]);

  const proposals = view?.targetKey === targetKey ? view.proposals : undefined;
  return { proposals, refresh };
}
