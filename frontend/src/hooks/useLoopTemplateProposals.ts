import { boardHint, chatHint, useProposalList, type ProposalListKind } from "./useProposalList";
import { loopTemplateProposalService } from "../services/auth";
import type { LoopTemplateProposal } from "../types";

export type LoopTemplateProposalsTarget =
  | { workItemId: string }
  | { chatSessionId: string }
  | { templateId: string };

type Mode = "item" | "template" | "chat";

function modeOf(target: LoopTemplateProposalsTarget) {
  if ("workItemId" in target) return ["item", target.workItemId] as const;
  if ("templateId" in target) return ["template", target.templateId] as const;
  return ["chat", target.chatSessionId] as const;
}

const loopTemplateProposals: ProposalListKind<Mode, LoopTemplateProposal> = {
  key: "loop-template",
  read: (mode, id) =>
    loopTemplateProposalService.list(
      mode === "item"
        ? { workItemId: id }
        : mode === "template"
          ? { templateId: id, status: "Pending" }
          : { chatSessionId: id },
    ),
  hint: (mode, id) =>
    mode === "chat"
      ? chatHint("ChatLoopTemplateProposalsChanged", id)
      : boardHint("LoopTemplateProposalsChanged", (p) => mode === "item" || p.templateId === id),
};

/**
 * The loop template proposals one work item's loop runs made, those one chat
 * made, or those pending for one template, kept current by snapshot reads: on
 * every (re)connect once the hub group is joined, on a hint for this target, and
 * on `refresh`. Any proposal hint reaches a work item, since it does not say
 * which run made it.
 */
export function useLoopTemplateProposals(target: LoopTemplateProposalsTarget) {
  const [mode, id] = modeOf(target);
  return useProposalList(loopTemplateProposals, mode, id);
}
