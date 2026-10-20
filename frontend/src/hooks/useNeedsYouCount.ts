import { workItemService } from "../services/auth";
import { WorkItemStatus } from "../types";
import { usePendingProposalCount, type ProposalCountKind } from "./usePendingProposalCount";
import type { SignalRConnection } from "./useSignalR";

// null until the server has answered, and after a read that fails, so the
// badge shows no number rather than a guessed one.
const needsYou: ProposalCountKind<number | null> = {
  key: "needs-you",
  read: async () => (await workItemService.getCounts({}))[WorkItemStatus.HumanFeedback] ?? 0,
  empty: null,
  hint: "WorkItemStateChanged",
};

/**
 * How many work items wait in Needs you, across the whole board, read on every
 * (re)connect and again whenever an item changes status, on `hub` or else the
 * app's shared work-item hub.
 */
export function useNeedsYouCount(hub?: SignalRConnection): number | null {
  return usePendingProposalCount(needsYou, hub);
}
