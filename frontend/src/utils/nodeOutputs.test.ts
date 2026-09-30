import { describe, expect, test } from "vite-plus/test";
import type { Edge, Node } from "@xyflow/react";
import { EdgeType, NodeType } from "../types";
import {
  hasSettingsProblems,
  mergeOutputs,
  nodeSettingsProblems,
  outputReferences,
  namedOutputNames,
  outputRenames,
  outputRowProblems,
  outputVisibilityOf,
  readFixedOutputs,
  wiredOutputsOf,
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

  test("a blank name is not a rename: the output keeps its loaded name until it is fixed", () => {
    expect([...outputRenames([row("  ", "a"), row("c", "b")])]).toEqual([["b", "c"]]);
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

  test("drops a deleted output", () => {
    const previous = [{ name: "OnFailure" }, { name: "gone", color: "x" }];
    expect(mergeOutputs(previous, [], [])).toEqual([{ name: "OnFailure" }]);
  });

  test("a deleted output's name typed into a rule is declared again as a new output", () => {
    const previous = [{ name: "OnFailure" }, { name: "gone", color: "x" }];
    expect(mergeOutputs(previous, [], ["gone"])).toEqual([{ name: "OnFailure" }, { name: "gone" }]);
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

  test("a blanked row still answers to the name it was loaded under, and never to a blank reference", () => {
    const rows = [row("", "a")];
    const references = outputReferences(rows[0], rows, {
      ...node,
      matchRules: [
        { pattern: "A", edgeName: "a" },
        { pattern: "", edgeName: "" },
      ],
      defaultEdge: "",
    });
    expect(references.matchRules).toEqual([0]);
    expect(references.defaultEdge).toBe(false);
    expect(references.wired).toEqual([{ name: "a", targetLabel: "Fix" }]);
  });
});

describe("wiredOutputsOf", () => {
  const nodes = [
    { id: "n-ai", data: { label: "Reviewer" } },
    { id: "n-fix", data: { label: "Fix it" } },
    { id: "n-nolabel", data: {} },
  ] as Node[];
  const edge = (id: string, source: string, target: string, edgeType: EdgeType, name?: string) =>
    ({ id, source, target, data: { edgeType, name } }) as Edge;

  test("lists the Custom edges leaving the node with the label of the node each reaches", () => {
    const edges = [
      edge("e1", "n-ai", "n-fix", EdgeType.Custom, "reject"),
      edge("e2", "n-ai", "n-fix", EdgeType.OnSuccess),
      edge("e3", "n-fix", "n-ai", EdgeType.Custom, "again"),
      edge("e4", "n-ai", "n-nolabel", EdgeType.Custom, "park"),
      edge("e5", "n-ai", "n-fix", EdgeType.Custom, ""),
    ];
    expect(wiredOutputsOf("n-ai", edges, nodes)).toEqual([
      { name: "reject", targetLabel: "Fix it" },
      { name: "park", targetLabel: "n-nolabel" },
    ]);
  });
});

describe("nodeSettingsProblems", () => {
  const settings = {
    rows: [row("reject", "reject")],
    fixed: [],
    matchRules: [{ pattern: "REJECT", edgeName: "reject" }],
    cases: [{ variant: "PrExists", subject: "", pattern: "", tag: "", edgeName: "has-pr" }],
    defaultEdge: "otherwise",
  };

  test("settings that resolve have no problems", () => {
    expect(hasSettingsProblems(nodeSettingsProblems(NodeType.AI, settings))).toBe(false);
    expect(hasSettingsProblems(nodeSettingsProblems(NodeType.Condition, settings))).toBe(false);
  });

  test("a new name is fine: saving declares it", () => {
    const problems = nodeSettingsProblems(NodeType.AI, {
      ...settings,
      matchRules: [{ pattern: "NEW", edgeName: "brand-new" }],
    });
    expect(problems.matchRules).toEqual([null]);
  });

  test("a rule with a pattern and no output, or routing to success/failure, is a problem", () => {
    const problems = nodeSettingsProblems(NodeType.AI, {
      ...settings,
      matchRules: [
        { pattern: "X", edgeName: " " },
        { pattern: "Y", edgeName: "OnFailure" },
        { pattern: "", edgeName: "" },
      ],
    });
    expect(problems.matchRules).toEqual([
      "Name the output this rule routes to.",
      "'OnFailure' is taken by the success and failure edges; route to a named output.",
      null,
    ]);
    expect(hasSettingsProblems(problems)).toBe(true);
  });

  test("a case or default with no output, or routing to success/failure, is a problem", () => {
    const problems = nodeSettingsProblems(NodeType.Condition, {
      ...settings,
      cases: [
        { ...settings.cases[0], edgeName: "" },
        { ...settings.cases[0], edgeName: "OnSuccess" },
      ],
      defaultEdge: "  ",
    });
    expect(problems.cases).toEqual([
      "Name the output this case routes to.",
      "'OnSuccess' is taken by the success and failure edges; route to a named output.",
    ]);
    expect(problems.defaultEdge).toBe(
      "A default output is required: name the output taken when no case matches.",
    );
  });

  test("rules are checked only on AI nodes, and cases and the default only on Conditions", () => {
    const broken = {
      ...settings,
      matchRules: [{ pattern: "X", edgeName: "" }],
      cases: [{ ...settings.cases[0], edgeName: "" }],
      defaultEdge: "",
    };
    expect(hasSettingsProblems(nodeSettingsProblems(NodeType.Human, broken))).toBe(false);
  });

  test("an output row problem blocks saving too", () => {
    expect(
      hasSettingsProblems(
        nodeSettingsProblems(NodeType.Human, { ...settings, rows: [row("", "a")] }),
      ),
    ).toBe(true);
  });
});

describe("outputVisibilityOf", () => {
  const RESERVED = [
    "on_rejected",
    "on_merge_conflict",
    "on_ci_failed",
    "on_comment",
    "on_approved",
    "on_ci_passed",
    "on_merged",
    "on_abandoned",
  ];
  const fixed = readFixedOutputs({
    Human: [{ name: "OnSuccess" }, { name: "OnFailure" }],
    Cmd: [{ name: "OnSuccess" }, { name: "OnFailure" }],
    PR: [
      { name: "OnSuccess" },
      { name: "OnFailure" },
      ...RESERVED.map((name) => ({ name, reserved: true })),
    ],
  });

  test("with no visible fields a PR node hides exactly its reserved outputs", () => {
    const isVisible = outputVisibilityOf(
      NodeType.PR,
      {
        outputs: [{ name: "OnSuccess" }, { name: "deploy" }, { name: "on_merged", reserved: true }],
      },
      fixed,
    );
    for (const name of RESERVED) expect(isVisible(name)).toBe(false);
    expect(["OnSuccess", "OnFailure", "deploy"].map(isVisible)).toEqual([true, true, true]);
  });

  test("a config with no outputs at all still gets the defaults", () => {
    expect(outputVisibilityOf(NodeType.PR, {}, fixed)("on_ci_failed")).toBe(false);
    expect(outputVisibilityOf(NodeType.PR, {}, fixed)("OnSuccess")).toBe(true);
    expect(outputVisibilityOf(NodeType.Cmd, {}, fixed)("OnFailure")).toBe(true);
  });

  test("a boolean visible on the output wins over the default, either way, on any output", () => {
    const pr = outputVisibilityOf(
      NodeType.PR,
      {
        outputs: [
          { name: "OnSuccess", visible: false },
          { name: "on_merged", reserved: true, visible: true },
          { name: "on_abandoned", reserved: true, visible: false },
          { name: "deploy", visible: false },
        ],
      },
      fixed,
    );
    expect(["OnSuccess", "on_merged", "on_abandoned", "deploy", "OnFailure"].map(pr)).toEqual([
      false,
      true,
      false,
      false,
      true,
    ]);
    const cmd = outputVisibilityOf(
      NodeType.Cmd,
      { outputs: [{ name: "OnSuccess" }, { name: "OnFailure", visible: false }] },
      fixed,
    );
    expect([cmd("OnSuccess"), cmd("OnFailure")]).toEqual([true, false]);
  });

  test.each([["false"], [0], [null], [{}]])(
    "a visible of %j is not a boolean, so the default applies",
    (visible) => {
      const isVisible = outputVisibilityOf(
        NodeType.PR,
        {
          outputs: [
            { name: "deploy", visible },
            { name: "on_merged", reserved: true, visible },
          ],
        },
        fixed,
      );
      expect(isVisible("deploy")).toBe(true);
      expect(isVisible("on_merged")).toBe(false);
    },
  );

  test("an output named like a reserved one is visible on a node that is not a PR node", () => {
    const isVisible = outputVisibilityOf(
      NodeType.Human,
      { outputs: [{ name: "on_merged" }, { name: "on_ci_failed" }] },
      fixed,
    );
    expect([isVisible("on_merged"), isVisible("on_ci_failed")]).toEqual([true, true]);
  });

  test("names are matched exactly, including the names of Object.prototype members", () => {
    const isVisible = outputVisibilityOf(
      NodeType.Human,
      {
        outputs: [
          { name: "constructor", visible: false },
          { name: "toString" },
          { name: "__proto__", visible: false },
          { name: "needs more work", visible: false },
          { name: "success", visible: false },
        ],
      },
      fixed,
    );
    expect(
      [
        "constructor",
        "toString",
        "__proto__",
        "hasOwnProperty",
        "needs more work",
        "success",
        "OnSuccess",
        "Success",
      ].map(isVisible),
    ).toEqual([false, true, false, true, false, false, true, true]);
  });

  test("a PR output its config flags reserved is hidden even when the fixed outputs are unknown", () => {
    const isVisible = outputVisibilityOf(
      NodeType.PR,
      { outputs: [{ name: "on_merged", reserved: true }, { name: "deploy" }] },
      new Map(),
    );
    expect([isVisible("on_merged"), isVisible("deploy")]).toEqual([false, true]);
  });
});

describe("namedOutputNames", () => {
  test("a hidden output can still be wired", () => {
    const node = {
      id: "h",
      position: { x: 0, y: 0 },
      data: {
        type: NodeType.Human,
        config: { outputs: [{ name: "later", visible: false }, { name: "now" }] },
      },
    } as Node;
    expect(namedOutputNames(node, new Map())).toEqual(["later", "now"]);
  });
});
