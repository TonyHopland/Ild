import { describe, expect, test } from "vite-plus/test";
import type { Edge, Node } from "@xyflow/react";
import { checkEdgeConstraints, getCustomEdgeNames, buildEdge } from "./edgeUtils";
import { EdgeType, NodeType } from "../types";

function node(id: string, type: NodeType, config: Record<string, unknown> = {}): Node {
  return { id, position: { x: 0, y: 0 }, data: { type, config } } as Node;
}

function edge(source: string, edgeType: EdgeType, name?: string): Edge {
  return {
    id: `${source}-${edgeType}-${name ?? ""}`,
    source,
    target: "t",
    data: { edgeType, name },
  } as Edge;
}

describe("getCustomEdgeNames", () => {
  test("derives AI node names from its match rules' edge names, deduped", () => {
    const ai = node("a", NodeType.AI, {
      matchRules: [
        { pattern: "REJECT", edgeName: "Reject" },
        { pattern: "ESCALATE", edgeName: "Escalate" },
        { pattern: "again", edgeName: "Reject" },
        { pattern: "blank", edgeName: "" },
      ],
    });
    expect(getCustomEdgeNames(ai)).toEqual(["Reject", "Escalate"]);
  });

  test("derives Human node names from its customEdges list", () => {
    const human = node("h", NodeType.Human, { customEdges: ["Respond", "Escalate"] });
    expect(getCustomEdgeNames(human)).toEqual(["Respond", "Escalate"]);
  });

  test("returns no names for a node type that cannot have custom edges", () => {
    expect(getCustomEdgeNames(node("c", NodeType.Cmd))).toEqual([]);
  });
});

describe("checkEdgeConstraints", () => {
  test("allows any number of custom edges on an AI node", () => {
    const existing = [edge("a", EdgeType.Custom, "Reject"), edge("a", EdgeType.Custom, "Escalate")];
    const result = checkEdgeConstraints("a", NodeType.AI, EdgeType.Custom, existing);
    expect(result.allowed).toBe(true);
  });

  test("rejects custom edges from a node type that cannot have them", () => {
    const result = checkEdgeConstraints("c", NodeType.Cmd, EdgeType.Custom, []);
    expect(result.allowed).toBe(false);
    expect(result.error).toContain("Human, AI and PR");
  });

  test("rejects a second OnSuccess edge from the same node", () => {
    const result = checkEdgeConstraints("a", NodeType.AI, EdgeType.OnSuccess, [
      edge("a", EdgeType.OnSuccess),
    ]);
    expect(result.allowed).toBe(false);
  });
});

describe("buildEdge", () => {
  test("carries the name only for custom edges and labels it by name", () => {
    const custom = buildEdge({
      source: "a",
      target: "b",
      edgeType: EdgeType.Custom,
      name: "Escalate",
      maxTraversals: null,
      sourceHandle: "respond",
      targetHandle: "target-handle",
    });
    expect((custom.data as { name?: string }).name).toBe("Escalate");
    expect(custom.label).toBe("Escalate");

    const success = buildEdge({
      source: "a",
      target: "b",
      edgeType: EdgeType.OnSuccess,
      name: "ignored",
      maxTraversals: null,
      sourceHandle: "success",
      targetHandle: "target-handle",
    });
    expect((success.data as { name?: string | null }).name).toBeNull();
    expect(success.label).toBe("success");
  });
});
