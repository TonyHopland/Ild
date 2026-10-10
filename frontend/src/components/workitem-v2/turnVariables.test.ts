import { describe, expect, test } from "vite-plus/test";
import type { RunConversationMessage, TurnVariableChange } from "../../types";
import { variablesSetByTurn } from "./turnVariables";

const turn = (
  runNodeId: string | null,
  role: RunConversationMessage["role"] = "ai",
): RunConversationMessage => ({
  id: 1,
  runId: "run-1",
  role,
  text: "done",
  timestamp: "2026-09-24T10:05:01Z",
  name: "Developer",
  runNodeId,
});

const change = (runNodeId: string, name: string): TurnVariableChange => ({
  runId: "run-1",
  runNodeId,
  name,
  value: `${name} value`,
  change: "created",
  changedLater: false,
});

const changes = [
  change("exec-1", "summary"),
  change("exec-1", "handoff"),
  change("exec-2", "summary"),
];

describe("variablesSetByTurn", () => {
  test("gives a turn exactly the changes its execution made, by name", () => {
    expect(variablesSetByTurn(turn("exec-1"), changes).map((c) => c.name)).toEqual([
      "handoff",
      "summary",
    ]);
  });

  test("an entry that does not name its execution shows nothing rather than a guess", () => {
    expect(variablesSetByTurn(turn(null), changes)).toEqual([]);
  });

  test("a turn whose execution changed nothing has no variables", () => {
    expect(variablesSetByTurn(turn("exec-3"), changes)).toEqual([]);
  });

  test("only AI turns carry variables", () => {
    expect(variablesSetByTurn(turn("exec-1", "human"), changes)).toEqual([]);
    expect(variablesSetByTurn(turn("exec-1", "system"), changes)).toEqual([]);
  });
});
