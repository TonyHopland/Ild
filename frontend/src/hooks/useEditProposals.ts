import { boardHint, chatHint, useProposalList, type ProposalListKind } from "./useProposalList";
import { workItemService } from "../services/auth";
import type { WorkItemEditProposal } from "../types";

export type EditProposalsTarget =
  | { workItemId: string }
  | { requestedByWorkItemId: string }
  | { chatSessionId: string };

type Mode = "item" | "requester" | "chat";

function modeOf(target: EditProposalsTarget) {
  if ("workItemId" in target) return ["item", target.workItemId] as const;
  if ("requestedByWorkItemId" in target)
    return ["requester", target.requestedByWorkItemId] as const;
  return ["chat", target.chatSessionId] as const;
}

const editProposals: ProposalListKind<Mode, WorkItemEditProposal> = {
  key: "edit",
  read: (mode, id) => {
    if (mode === "item") return workItemService.listEditProposals(id);
    if (mode === "requester") return workItemService.listRequestedEditProposals(id);
    return workItemService.listEditProposalsFor({ chatSessionId: id });
  },
  hint: (mode, id) =>
    mode === "chat"
      ? chatHint("ChatEditProposalsChanged", id)
      : boardHint("WorkItemEditProposalsChanged", (payload) => payload.workItemId === id),
};

/**
 * The edit proposals for one work item, those its loop runs requested on any
 * item, or those made by one chat, kept current by snapshot reads: on every
 * (re)connect once the hub group is joined, on a hint for this target, and on
 * `refresh`. A requesting item is hinted as a work item. Work items are heard on
 * the app's shared work-item hub; a chat on a chat hub connection of its own.
 */
export function useEditProposals(target: EditProposalsTarget) {
  const [mode, id] = modeOf(target);
  return useProposalList(editProposals, mode, id);
}
