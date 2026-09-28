import type { ChatMessage, ConversationMessage, WorkItemEditProposal } from "../types";

/** Oldest first, by the WorkItem server's clock, then by id. */
function byCreation(a: WorkItemEditProposal, b: WorkItemEditProposal): number {
  return (
    Date.parse(a.createdAt) - Date.parse(b.createdAt) || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0)
  );
}

function push<K>(slots: Map<K, WorkItemEditProposal[]>, key: K, proposal: WorkItemEditProposal) {
  const slot = slots.get(key);
  if (slot) slot.push(proposal);
  else slots.set(key, [proposal]);
}

function lastIndexWhere<T>(items: T[], match: (item: T) => boolean): number {
  for (let i = items.length - 1; i >= 0; i--) if (match(items[i])) return i;
  return -1;
}

function sortSlots<K>(slots: Map<K, WorkItemEditProposal[]>) {
  for (const slot of slots.values()) slot.sort(byCreation);
}

export interface ChatPlacement {
  /** The cards that name a reply older than every message: before the transcript. */
  head: WorkItemEditProposal[];
  /** The cards that follow each message, by message id. */
  afterMessage: Map<string, WorkItemEditProposal[]>;
  /**
   * The cards after the whole transcript: those whose reply has not landed
   * yet, then those that name no reply.
   */
  tail: WorkItemEditProposal[];
}

/**
 * Where a chat's cards go among its `messages` (sorted by sequence). A card
 * follows the last message at or before the reply it names. While that reply
 * is still in flight nothing follows it yet, so the card goes to the tail,
 * which renders after the streaming text, where the reply will land.
 */
export function placeChatProposals(
  messages: ChatMessage[],
  proposals: WorkItemEditProposal[],
): ChatPlacement {
  const head: WorkItemEditProposal[] = [];
  const afterMessage = new Map<string, WorkItemEditProposal[]>();
  const pending: WorkItemEditProposal[] = [];
  const unanchored: WorkItemEditProposal[] = [];
  for (const proposal of proposals) {
    const anchor = proposal.chatReplySequence;
    if (anchor == null) {
      unanchored.push(proposal);
      continue;
    }
    const at = lastIndexWhere(messages, (m) => m.sequence <= anchor);
    if (at < 0 && messages.length > 0) head.push(proposal);
    else if (at >= 0 && (messages[at].sequence === anchor || at < messages.length - 1))
      push(afterMessage, messages[at].id, proposal);
    else pending.push(proposal);
  }
  sortSlots(afterMessage);
  return {
    head: head.sort(byCreation),
    afterMessage,
    tail: [...pending.sort(byCreation), ...unanchored.sort(byCreation)],
  };
}

export interface ActionPlacement {
  /** The cards that follow each conversation turn, by its index. */
  afterTurn: Map<number, WorkItemEditProposal[]>;
  /** The cards whose step has no turn yet: they follow the live bubble. */
  live: WorkItemEditProposal[];
  /** The cards that name no step: they close the thread. */
  end: WorkItemEditProposal[];
}

/**
 * Where the cards a work item's loop runs asked for, on whichever item, go in
 * its Action thread. A card follows the last turn its step wrote. A chat's
 * cards belong to that chat and are left out.
 */
export function placeActionProposals(
  messages: ConversationMessage[],
  proposals: WorkItemEditProposal[],
): ActionPlacement {
  const afterTurn = new Map<number, WorkItemEditProposal[]>();
  const live: WorkItemEditProposal[] = [];
  const end: WorkItemEditProposal[] = [];
  for (const proposal of proposals) {
    if (!proposal.createdByLoopRunId) continue;
    const step = proposal.createdByRunNodeId;
    if (!step) {
      end.push(proposal);
      continue;
    }
    const turn = lastIndexWhere(messages, (m) => m.runNodeId === step);
    if (turn >= 0) push(afterTurn, turn, proposal);
    else live.push(proposal);
  }
  sortSlots(afterTurn);
  return { afterTurn, live: live.sort(byCreation), end: end.sort(byCreation) };
}
