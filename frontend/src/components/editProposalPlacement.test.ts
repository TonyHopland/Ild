import { describe, expect, test } from "vite-plus/test";
import { placeChatProposals } from "./editProposalPlacement";
import type { ChatMessage, WorkItemEditProposal } from "../types";

function message(sequence: number): ChatMessage {
  const role = sequence % 2 === 0 ? "user" : "assistant";
  return { id: `m${sequence}`, role, content: "", interrupted: false, sequence, createdAt: "" };
}

function proposal(id: string, chatReplySequence: number): WorkItemEditProposal {
  return {
    id,
    workItemId: "wi-1",
    status: "Pending",
    proposed: { title: id },
    snapshot: {
      title: "t",
      description: null,
      tags: [],
      branchNameOverride: null,
      baseBranchOverride: null,
    },
    rationale: null,
    rejectionReason: null,
    createdByLoopRunId: null,
    createdByChatSessionId: "s1",
    chatReplySequence,
    createdAt: "2026-09-26T10:00:00Z",
    decidedAt: null,
  };
}

const ids = (list: WorkItemEditProposal[] | undefined) => (list ?? []).map((p) => p.id);

describe("placeChatProposals", () => {
  test("a card whose reply has landed follows it, even as the transcript's last message", () => {
    const placed = placeChatProposals([message(0), message(1)], [proposal("p", 1)]);

    expect(ids(placed.afterMessage.get("m1"))).toEqual(["p"]);
    expect(placed.tail).toEqual([]);
  });

  test("a card naming a reply older than every loaded message goes before them all", () => {
    const placed = placeChatProposals([message(4), message(5)], [proposal("p", 2)]);

    expect(ids(placed.head)).toEqual(["p"]);
    expect(placed.afterMessage.size).toBe(0);
    expect(placed.tail).toEqual([]);
  });
});
