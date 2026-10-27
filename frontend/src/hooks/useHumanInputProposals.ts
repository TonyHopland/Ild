import { boardHint, useProposalList, type ProposalListKind } from "./useProposalList";
import { humanInputProposalService } from "../services/auth";
import type { HumanInputProposal } from "../types";

const humanInputProposals: ProposalListKind<"item", HumanInputProposal> = {
  key: "human-input",
  read: (_mode, workItemId) => humanInputProposalService.list(workItemId),
  hint: (_mode, workItemId) =>
    boardHint("HumanInputProposalsChanged", (payload) => payload.workItemId === workItemId),
};

/**
 * The suggested answers for one work item's parked question, whichever run or
 * chat made them, kept current by snapshot reads: on every (re)connect once the
 * hub group is joined, on a hint for this work item, and on `refresh`.
 */
export function useHumanInputProposals(workItemId: string) {
  return useProposalList(humanInputProposals, "item", workItemId);
}
