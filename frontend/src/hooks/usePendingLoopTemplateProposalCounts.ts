import { usePendingProposalCount, type ProposalCountKind } from "./usePendingProposalCount";
import { loopTemplateProposalService } from "../services/auth";

const NONE: Record<string, number> = {};

const pendingLoopTemplateProposals: ProposalCountKind<Record<string, number>> = {
  key: "loop-template",
  read: async () => (await loopTemplateProposalService.pendingCounts()) ?? NONE,
  empty: NONE,
  hint: "LoopTemplateProposalsChanged",
};

/**
 * How many loop template proposals wait for a human, per template id, read on
 * every (re)connect once the hub group is joined and on every proposal hint.
 * Only the latest read counts; one that fails shows no counts rather than old ones.
 */
export function usePendingLoopTemplateProposalCounts(): Record<string, number> {
  return usePendingProposalCount(pendingLoopTemplateProposals);
}
