import { usePendingProposalCount } from "./usePendingProposalCount";
import { globalVariableService } from "../services/auth";

const readCount = () => globalVariableService.pendingProposalCount();

/**
 * How many global variable suggestions wait for a human, read on every
 * (re)connect once the hub group is joined and on every suggestion hint. Only
 * the latest read counts; one that fails shows no count rather than an old one.
 */
export function usePendingGlobalVariableProposalCount(): number {
  return usePendingProposalCount(readCount, "GlobalVariableProposalsChanged", 0);
}
