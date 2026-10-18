import { boardHint, chatHint, useProposalList, type ProposalListKind } from "./useProposalList";
import { globalVariableService } from "../services/auth";
import type { GlobalVariableProposal } from "../types";

export type GlobalVariableProposalsTarget = { workItemId: string } | { chatSessionId: string };

type Mode = "item" | "chat";

function modeOf(target: GlobalVariableProposalsTarget) {
  if ("workItemId" in target) return ["item", target.workItemId] as const;
  return ["chat", target.chatSessionId] as const;
}

const globalVariableProposals: ProposalListKind<Mode, GlobalVariableProposal> = {
  key: "global-variable",
  read: (mode, id) =>
    globalVariableService.listProposals(
      mode === "item" ? { workItemId: id } : { chatSessionId: id },
    ),
  hint: (mode, id) =>
    mode === "chat"
      ? chatHint("ChatGlobalVariableProposalsChanged", id)
      : boardHint("GlobalVariableProposalsChanged"),
};

/**
 * The global variable suggestions one work item's loop runs made, or those one
 * chat made, kept current by snapshot reads: on every (re)connect once the hub
 * group is joined, on a hint for this target, and on `refresh`. Any suggestion
 * hint reaches a work item, since it does not say which run made it.
 */
export function useGlobalVariableProposals(target: GlobalVariableProposalsTarget) {
  const [mode, id] = modeOf(target);
  return useProposalList(globalVariableProposals, mode, id);
}
