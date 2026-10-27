import type { ChatMessage, RunConversationMessage } from "../types";
import { compareCreatedAt } from "../utils/createdAt";

/**
 * What placing a card needs. Work item edit proposals, global variable
 * suggestions and loop template proposals all carry it, so a thread places
 * every kind in one order.
 */
export interface PlacedProposal {
  id: string;
  createdAt: string;
  createdByLoopRunId: string | null;
  createdByRunNodeId?: string | null;
  chatReplySequence?: number | null;
}

/** Oldest first, by the server's clock, then by id. */
function byCreation(a: PlacedProposal, b: PlacedProposal): number {
  return compareCreatedAt(a.createdAt, b.createdAt) || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0);
}

function push<K, P>(slots: Map<K, P[]>, key: K, proposal: P) {
  const slot = slots.get(key);
  if (slot) slot.push(proposal);
  else slots.set(key, [proposal]);
}

function lastIndexWhere<T>(items: T[], match: (item: T) => boolean): number {
  for (let i = items.length - 1; i >= 0; i--) if (match(items[i])) return i;
  return -1;
}

function sortSlots<K, P extends PlacedProposal>(slots: Map<K, P[]>) {
  for (const slot of slots.values()) slot.sort(byCreation);
}

export interface ChatPlacement<P extends PlacedProposal> {
  /** The cards that name a reply older than every message: before the transcript. */
  head: P[];
  /** The cards that follow each message, by message id. */
  afterMessage: Map<string, P[]>;
  /**
   * The cards after the whole transcript: those whose reply has not landed
   * yet, then those that name no reply.
   */
  tail: P[];
}

/**
 * Where a chat's cards go among its `messages` (sorted by sequence). A card
 * follows the last message at or before the reply it names. While that reply
 * is still in flight nothing follows it yet, so the card goes to the tail,
 * which renders after the streaming text, where the reply will land.
 */
export function placeChatProposals<P extends PlacedProposal>(
  messages: ChatMessage[],
  proposals: P[],
): ChatPlacement<P> {
  const head: P[] = [];
  const afterMessage = new Map<string, P[]>();
  const pending: P[] = [];
  const unanchored: P[] = [];
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

export interface ActionPlacement<P extends PlacedProposal> {
  /** The cards that follow each conversation turn, by its index. */
  afterTurn: Map<number, P[]>;
  /** The cards whose step has no turn yet: they follow the live bubble. */
  live: P[];
  /** The cards that name no step, or come from another run: they close the thread. */
  end: P[];
}

/**
 * Where the cards a work item's loop runs asked for, on whichever item, go in
 * the Action thread of run `runId`, whose conversation is `messages`. A card
 * follows the last turn its step wrote. Another run's cards have no turn here,
 * so they close the thread, where they can still be decided. A chat's cards
 * belong to that chat and are left out.
 */
export function placeActionProposals<P extends PlacedProposal>(
  runId: string | null,
  messages: RunConversationMessage[],
  proposals: P[],
): ActionPlacement<P> {
  const afterTurn = new Map<number, P[]>();
  const live: P[] = [];
  const end: P[] = [];
  for (const proposal of proposals) {
    if (!proposal.createdByLoopRunId) continue;
    const step = proposal.createdByRunNodeId;
    if (!step || proposal.createdByLoopRunId !== runId) {
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

export interface HumanInputPlacement<P extends PlacedProposal> {
  /** The cards made before the first turn: before the thread. */
  head: P[];
  /** The cards that follow each conversation turn, by its index. */
  afterTurn: Map<number, P[]>;
  /** The card for the question the run waits on now: just before the buttons that answer it. */
  open: P[];
}

/**
 * Where the suggested answers for the questions of a run go among its
 * conversation `messages`. The suggestion for the question the run waits on
 * (`waitingRunNodeId`) goes in `open`, wherever it was made. Every other one
 * stays where it was made, after the last turn at or before it.
 */
export function placeHumanInputProposals<P extends PlacedProposal & { runNodeId: string }>(
  messages: RunConversationMessage[],
  proposals: P[],
  waitingRunNodeId: string | null | undefined,
): HumanInputPlacement<P> {
  const head: P[] = [];
  const afterTurn = new Map<number, P[]>();
  const open: P[] = [];
  for (const proposal of proposals) {
    if (proposal.runNodeId === waitingRunNodeId) {
      open.push(proposal);
      continue;
    }
    const turn = lastIndexWhere(
      messages,
      (m) => compareCreatedAt(m.timestamp, proposal.createdAt) <= 0,
    );
    if (turn >= 0) push(afterTurn, turn, proposal);
    else head.push(proposal);
  }
  sortSlots(afterTurn);
  return { head: head.sort(byCreation), afterTurn, open: open.sort(byCreation) };
}
