import { describe, expect, test } from "vite-plus/test";
import type { ConversationMessage, LoopRunVariableWrite } from "../../types";
import { variablesSetByTurn } from "./turnVariables";

const turn = (runNodeId: string | null, role = "ai"): ConversationMessage => ({
  role,
  content: "done",
  timestamp: "2026-09-24T10:05:01Z",
  name: "Developer",
  runNodeId,
});

const write = (
  name: string,
  value: string,
  previousValue: string | null,
  runNodeId: string,
  runId = "run-1",
): LoopRunVariableWrite => ({
  runId,
  runNodeId,
  name,
  value,
  previousValue,
  writtenAt: "2026-09-24T10:00:00Z",
});

// Two turns of the same node: the first creates `summary`, the second changes it.
const twoTurns = [
  write("summary", "draft", null, "exec-1"),
  write("summary", "first pass", "draft", "exec-1"),
  write("summary", "final", "first pass", "exec-2"),
];

describe("variablesSetByTurn", () => {
  test("shows the value a turn left, not the variable's current value", () => {
    expect(variablesSetByTurn(turn("exec-1"), twoTurns)).toEqual([
      { name: "summary", value: "first pass", change: "created", changedLater: true },
    ]);
  });

  test("marks a later turn's write as a change", () => {
    expect(variablesSetByTurn(turn("exec-2"), twoTurns)).toEqual([
      { name: "summary", value: "final", change: "changed", changedLater: false },
    ]);
  });

  test("a variable set before history was kept is a change, not new", () => {
    // Its only recorded write replaced a value no history row holds.
    const upgraded = [write("summary", "after", "before the upgrade", "exec-1")];
    expect(variablesSetByTurn(turn("exec-1"), upgraded)).toEqual([
      { name: "summary", value: "after", change: "changed", changedLater: false },
    ]);
  });

  test("a later change in another run is not a later change of this variable", () => {
    const retried = [...twoTurns, write("summary", "retry", null, "exec-9", "run-2")];
    const [v] = variablesSetByTurn(turn("exec-2"), retried);
    expect(v.changedLater).toBe(false);
  });

  test("an entry that does not name its execution shows nothing rather than a guess", () => {
    expect(variablesSetByTurn(turn(null), twoTurns)).toEqual([]);
  });

  test("a turn that wrote nothing has no variables", () => {
    expect(variablesSetByTurn(turn("exec-3"), twoTurns)).toEqual([]);
  });

  test("a turn that left a variable as it found it did not change it", () => {
    const roundTrip = [
      write("summary", "temp", "same", "exec-1"),
      write("summary", "same", "temp", "exec-1"),
    ];
    expect(variablesSetByTurn(turn("exec-1"), roundTrip)).toEqual([]);
  });

  test("human turns never carry variables", () => {
    expect(variablesSetByTurn(turn("exec-1", "human"), twoTurns)).toEqual([]);
  });
});
