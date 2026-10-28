import { describe, expect, test } from "vite-plus/test";
import { placeChatProposals, placeHumanInputProposals } from "./editProposalPlacement";
import type {
  ChatMessage,
  HumanInputProposal,
  RunConversationMessage,
  WorkItemEditProposal,
} from "../types";
import { makeEditProposal } from "../test-support";

function message(sequence: number): ChatMessage {
  const role = sequence % 2 === 0 ? "user" : "assistant";
  return { id: `m${sequence}`, role, content: "", interrupted: false, sequence, createdAt: "" };
}

function proposal(id: string, chatReplySequence: number): WorkItemEditProposal {
  return makeEditProposal({
    id,
    proposed: { title: id },
    createdByChatSessionId: "s1",
    chatReplySequence,
  });
}

const ids = (list: { id: string }[] | undefined) => (list ?? []).map((p) => p.id);

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

describe("card order within a slot", () => {
  test("follows the server's clock below the millisecond, not the ids", () => {
    const later = { ...proposal("a", 1), createdAt: "2026-09-26T10:00:00.0004002Z" };
    const earlier = { ...proposal("b", 1), createdAt: "2026-09-26T10:00:00.0004001Z" };

    const placed = placeChatProposals([message(0), message(1)], [later, earlier]);

    expect(ids(placed.afterMessage.get("m1"))).toEqual(["b", "a"]);
  });
});

function turn(id: number, timestamp: string): RunConversationMessage {
  return {
    id,
    runId: "run-1",
    runNodeId: "exec-1",
    role: "ai",
    name: "Coder",
    text: "",
    timestamp,
  };
}

function suggestion(id: string, runNodeId: string, createdAt: string): HumanInputProposal {
  return {
    id,
    workItemId: "wi-1",
    runId: "run-1",
    runNodeId,
    output: "OnSuccess",
    text: "",
    rationale: "r",
    status: "Pending",
    rejectionReason: null,
    decidedBy: null,
    createdAt,
    decidedAt: null,
    createdByLoopRunId: "run-9",
    createdByChatSessionId: null,
  };
}

const turns = [
  turn(1, "2026-10-01T09:00:00Z"),
  turn(2, "2026-10-01T11:00:00Z"),
  turn(3, "2026-10-01T12:00:00Z"),
];

describe("placeHumanInputProposals", () => {
  test("the suggestion for the open question goes before the buttons even when a turn is later", () => {
    const placed = placeHumanInputProposals(
      turns,
      [suggestion("open", "exec-2", "2026-10-01T10:00:00Z")],
      "exec-2",
    );

    expect(ids(placed.open)).toEqual(["open"]);
    expect(placed.afterTurn.size).toBe(0);
    expect(placed.head).toEqual([]);
  });

  test("any other suggestion follows the last turn at or before it", () => {
    const placed = placeHumanInputProposals(
      turns,
      [
        suggestion("a", "exec-1", "2026-10-01T10:00:00Z"),
        suggestion("b", "exec-1", "2026-10-01T11:00:00Z"),
        suggestion("c", "exec-1", "2026-10-01T13:00:00Z"),
      ],
      "exec-2",
    );

    expect(ids(placed.afterTurn.get(0))).toEqual(["a"]);
    expect(ids(placed.afterTurn.get(1))).toEqual(["b"]);
    expect(ids(placed.afterTurn.get(2))).toEqual(["c"]);
    expect(placed.open).toEqual([]);
  });

  test("a suggestion made before the first turn goes before the thread", () => {
    const placed = placeHumanInputProposals(
      turns,
      [suggestion("early", "exec-1", "2026-10-01T08:00:00Z")],
      null,
    );

    expect(ids(placed.head)).toEqual(["early"]);
    expect(placed.afterTurn.size).toBe(0);
  });

  test("times that differ by less than a millisecond are ordered by the server's clock", () => {
    const placed = placeHumanInputProposals(
      [turn(1, "2026-10-01T09:00:00.1234568Z")],
      [
        suggestion("after", "exec-1", "2026-10-01T09:00:00.1234569Z"),
        suggestion("before", "exec-1", "2026-10-01T09:00:00.1234567Z"),
      ],
      null,
    );

    expect(ids(placed.head)).toEqual(["before"]);
    expect(ids(placed.afterTurn.get(0))).toEqual(["after"]);
  });
});
