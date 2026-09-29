import { describe, expect, test } from "vite-plus/test";
import {
  mergeOutputs,
  outputReferences,
  outputRenames,
  outputRowProblems,
  readFixedOutputs,
  type OutputRow,
} from "./nodeOutputs";

const row = (name: string, originalName: string | null, extra: Record<string, unknown> = {}) =>
  ({ output: { name, ...extra }, originalName }) as OutputRow;

describe("outputRenames", () => {
  test("maps each loaded output to its new name, including two names swapped", () => {
    const renames = outputRenames([row("b", "a"), row("a", "b"), row("same", "same")]);
    expect([...renames]).toEqual([
      ["a", "b"],
      ["b", "a"],
    ]);
  });

  test("ignores added rows and compares names trimmed", () => {
    expect([...outputRenames([row("new", null), row(" kept ", "kept")])]).toEqual([]);
  });
});

describe("mergeOutputs", () => {
  test("renames in place and keeps every other field", () => {
    const previous = [
      { name: "OnSuccess" },
      { name: "a", color: "x", visible: false },
      { name: "b" },
    ];
    expect(
      mergeOutputs(previous, [row("a2", "a", { color: "x", visible: false }), row("b", "b")], []),
    ).toEqual([{ name: "OnSuccess" }, { name: "a2", color: "x", visible: false }, { name: "b" }]);
  });

  test("swapping two names keeps each output's own fields and position", () => {
    const previous = [
      { name: "a", x: 1 },
      { name: "b", y: 2 },
    ];
    expect(mergeOutputs(previous, [row("b", "a", { x: 1 }), row("a", "b", { y: 2 })], [])).toEqual([
      { name: "b", x: 1 },
      { name: "a", y: 2 },
    ]);
  });

  test("drops a deleted output and never brings it back, even when still referenced", () => {
    const previous = [{ name: "OnFailure" }, { name: "gone", color: "x" }];
    expect(mergeOutputs(previous, [], ["gone"])).toEqual([{ name: "OnFailure" }]);
  });

  test("appends added rows, then referenced names the node never declared, once each", () => {
    const previous = [{ name: "a" }];
    expect(
      mergeOutputs(
        previous,
        [row("a", "a"), row(" added ", null)],
        ["new", "new", "a", "OnSuccess", ""],
      ),
    ).toEqual([{ name: "a" }, { name: "added" }, { name: "new" }]);
  });

  test("keeps entries that are not well-formed for the server to report", () => {
    const previous = [{ color: "x" }, "stray", { name: "" }];
    expect(mergeOutputs(previous, [], [])).toEqual(previous);
  });
});

describe("readFixedOutputs", () => {
  test("reads each node type's outputs into a map, keeping their fields", () => {
    const fixed = readFixedOutputs({
      AI: [{ name: "OnSuccess" }, { name: "OnFailure" }],
      PR: [{ name: "on_merged", reserved: true }],
      Cleanup: [],
    });
    expect(fixed).toBeInstanceOf(Map);
    expect(fixed.get("PR")).toEqual([{ name: "on_merged", reserved: true }]);
    expect(fixed.get("Cleanup")).toEqual([]);
    expect(fixed.get("AI")?.map((o) => o.name)).toEqual(["OnSuccess", "OnFailure"]);
  });

  test("skips values that are not lists and entries without a name", () => {
    const fixed = readFixedOutputs({
      AI: "nope",
      Human: [{ name: " " }, { other: 1 }, { name: "x" }],
    });
    expect(fixed.has("AI")).toBe(false);
    expect(fixed.get("Human")).toEqual([{ name: "x" }]);
  });

  test.each([null, undefined, "text", [1, 2]])("reads %s as no fixed outputs", (value) => {
    expect(readFixedOutputs(value).size).toBe(0);
  });
});

describe("outputRowProblems", () => {
  const reserved = [{ name: "on_merged", reserved: true }];

  test("a blank name is a problem, not a delete", () => {
    expect(outputRowProblems([row("  ", "a")], [])).toEqual(["Give the output a name."]);
  });

  test("only the row renamed onto a name another row still holds is flagged", () => {
    expect(outputRowProblems([row("a", "a"), row("a", "b")], [])).toEqual([
      null,
      "The node already has an output named 'a'. Pick another name.",
    ]);
  });

  test("two added rows with one name are both flagged", () => {
    expect(outputRowProblems([row("x", null), row(" x ", null)], []).every(Boolean)).toBe(true);
  });

  test.each(["OnSuccess", "OnFailure", "on_merged"])("%s is taken", (name) => {
    expect(outputRowProblems([row(name, null)], reserved)[0]).toContain(`'${name}'`);
  });

  test("a swap is fine once both rows have distinct names", () => {
    expect(outputRowProblems([row("b", "a"), row("a", "b")], [])).toEqual([null, null]);
  });
});

describe("outputReferences", () => {
  const node = {
    wired: [
      { name: "a", targetLabel: "Fix" },
      { name: "b", targetLabel: "Ship" },
    ],
    matchRules: [
      { pattern: "A", edgeName: "a" },
      { pattern: "B", edgeName: " b " },
    ],
    cases: [],
    defaultEdge: null,
  };

  test("finds the edges wired from the loaded name and the rules routing to the row", () => {
    const rows = [row("a", "a"), row("b", "b")];
    expect(outputReferences(rows[1], rows, node)).toEqual({
      wired: [{ name: "b", targetLabel: "Ship" }],
      matchRules: [1],
      cases: [],
      defaultEdge: false,
    });
  });

  test("follows this edit's renames, as saving does", () => {
    const rows = [row("b", "a"), row("a", "b")];
    const references = outputReferences(rows[0], rows, node);
    expect(references.wired).toEqual([{ name: "a", targetLabel: "Fix" }]);
    expect(references.matchRules).toEqual([0]);
  });

  test("a blank row is referenced by nothing but its own edges", () => {
    const rows = [row("", "a")];
    const references = outputReferences(rows[0], rows, {
      ...node,
      matchRules: [{ pattern: "", edgeName: "" }],
      defaultEdge: "",
    });
    expect(references.matchRules).toEqual([]);
    expect(references.defaultEdge).toBe(false);
    expect(references.wired).toEqual([{ name: "a", targetLabel: "Fix" }]);
  });
});
