import { describe, expect, test } from "vite-plus/test";
import type { ConversationMessage, LoopRun, LoopRunVariableWrite } from "../../types";
import { LoopRunStatus } from "../../types";
import { variablesSetByTurn } from "./turnVariables";

function run(variableWrites: LoopRunVariableWrite[] | undefined, id = "run-1"): LoopRun {
  return {
    id,
    workItemId: "1",
    loopTemplateId: "t",
    templateVersion: 1,
    status: LoopRunStatus.Running,
    currentNodeId: null,
    isPaused: false,
    nodeExecutionCount: 0,
    startedAt: "2026-09-24T10:00:00Z",
    completedAt: null,
    nodes: [],
    availableVariables: [
      { name: "summary", value: "final", createdAt: "2026-09-24T10:02:00Z", updatedAt: null },
    ],
    variableWrites,
  };
}

const turn = (runNodeId: string | null, role = "ai"): ConversationMessage => ({
  role,
  content: "done",
  timestamp: "2026-09-24T10:05:01Z",
  name: "Developer",
  runNodeId,
});

const write = (name: string, value: string, runNodeId: string, writtenAt: string) => ({
  name,
  value,
  runNodeId,
  writtenAt,
});

// Two turns of the same node: the first creates `summary`, the second changes it.
const twoTurns = run([
  write("summary", "draft", "exec-1", "2026-09-24T10:02:00Z"),
  write("summary", "first pass", "exec-1", "2026-09-24T10:04:00Z"),
  write("summary", "final", "exec-2", "2026-09-24T10:12:00Z"),
]);

describe("variablesSetByTurn", () => {
  test("shows the value a turn left, not the variable's current value", () => {
    expect(variablesSetByTurn(turn("exec-1"), [twoTurns])).toEqual([
      { name: "summary", value: "first pass", change: "created", changedLater: true },
    ]);
  });

  test("marks a later turn's write as a change", () => {
    expect(variablesSetByTurn(turn("exec-2"), [twoTurns])).toEqual([
      { name: "summary", value: "final", change: "changed", changedLater: false },
    ]);
  });

  test("finds the turn's writes in whichever run holds them", () => {
    const other = run([write("plan", "x", "exec-9", "2026-09-24T09:00:00Z")], "run-0");
    const [v] = variablesSetByTurn(turn("exec-2"), [other, twoTurns]);
    expect(v.value).toBe("final");
  });

  test("an entry that does not name its execution shows nothing rather than a guess", () => {
    expect(variablesSetByTurn(turn(null), [twoTurns])).toEqual([]);
  });

  test("a run from before variable history shows nothing, even with variables set", () => {
    expect(variablesSetByTurn(turn("exec-1"), [run(undefined)])).toEqual([]);
  });

  test("a turn that wrote nothing has no variables", () => {
    expect(variablesSetByTurn(turn("exec-3"), [twoTurns])).toEqual([]);
  });

  test("rewriting the same value is not a change", () => {
    const same = run([
      write("summary", "same", "exec-1", "2026-09-24T10:02:00Z"),
      write("summary", "same", "exec-2", "2026-09-24T10:12:00Z"),
    ]);
    expect(variablesSetByTurn(turn("exec-2"), [same])).toEqual([]);
  });

  test("human turns never carry variables", () => {
    expect(variablesSetByTurn(turn("exec-1", "human"), [twoTurns])).toEqual([]);
  });
});
