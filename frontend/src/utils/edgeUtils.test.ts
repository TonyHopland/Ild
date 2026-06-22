import { describe, expect, test } from "vite-plus/test";
import type { Edge, Node } from "@xyflow/react";
import {
  checkEdgeConstraints,
  getCustomEdgeNames,
  getConnectedCustomEdgeNames,
  buildEdge,
  appendEdge,
  parallelEdgeRoute,
  parallelLabelOffset,
  PARALLEL_LABEL_STAGGER,
  LOOP_EDGE_TYPE,
} from "./edgeUtils";
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

function routedEdge(
  id: string,
  source: string,
  target: string,
  sourceHandle: string,
  targetHandle: string,
): Edge {
  return { id, source, target, sourceHandle, targetHandle, data: {} } as Edge;
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

  test("PR node offers the seven reserved heartbeat edges plus any declared ones", () => {
    const pr = node("p", NodeType.PR, { customEdges: ["custom_extra"] });
    const names = getCustomEdgeNames(pr);
    for (const reserved of [
      "on_rejected",
      "on_merge_conflict",
      "on_ci_failed",
      "on_approved",
      "on_ci_passed",
      "on_merged",
      "on_abandoned",
    ]) {
      expect(names).toContain(reserved);
    }
    expect(names).toContain("custom_extra");
  });
});

describe("getConnectedCustomEdgeNames", () => {
  test("returns the names of custom edges wired out of the node, deduped", () => {
    const edges = [
      edge("h", EdgeType.Custom, "Respond"),
      edge("h", EdgeType.Custom, "Respond"),
      edge("h", EdgeType.OnSuccess),
      edge("h", EdgeType.OnFailure),
      edge("other", EdgeType.Custom, "Escalate"),
    ];
    expect(getConnectedCustomEdgeNames("h", edges)).toEqual(["Respond"]);
  });

  test("surfaces a connected custom edge even when the node declares none (seeded/migrated data)", () => {
    // The reviewer's case: a wired "Respond" edge with no `customEdges` config.
    const human = node("h", NodeType.Human, {});
    const declared = getCustomEdgeNames(human);
    const connected = getConnectedCustomEdgeNames("h", [edge("h", EdgeType.Custom, "Respond")]);
    expect(declared).toEqual([]);
    expect(connected).toEqual(["Respond"]);
  });

  test("ignores blank and whitespace-only custom edge names", () => {
    const edges = [edge("h", EdgeType.Custom, "   "), edge("h", EdgeType.Custom, undefined)];
    expect(getConnectedCustomEdgeNames("h", edges)).toEqual([]);
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

describe("parallelEdgeRoute", () => {
  test("a lone edge between two nodes is its own only sibling", () => {
    const only = routedEdge("e1", "a", "b", "respond", "target-handle");
    expect(parallelEdgeRoute([only], only)).toEqual({ index: 0, count: 1 });
  });

  test("edges sharing the same source/target route are siblings with stable indices", () => {
    // Two custom edges from one node into the same target node — the bug case.
    const reject = routedEdge("e-reject", "pr", "fix", "respond", "target-handle");
    const ciFailure = routedEdge("e-ci", "pr", "fix", "respond", "target-handle");
    const edges = [reject, ciFailure];

    expect(parallelEdgeRoute(edges, reject).count).toBe(2);
    expect(parallelEdgeRoute(edges, ciFailure).count).toBe(2);
    // Ordered by id, so the two edges occupy different lanes deterministically.
    expect(parallelEdgeRoute(edges, ciFailure).index).toBe(0);
    expect(parallelEdgeRoute(edges, reject).index).toBe(1);
  });

  test("edges to different targets or from different handles are not siblings", () => {
    const toFix = routedEdge("e1", "pr", "fix", "respond", "target-handle");
    const toReview = routedEdge("e2", "pr", "review", "respond", "target-handle");
    const fromSuccess = routedEdge("e3", "pr", "fix", "success", "target-handle");
    const edges = [toFix, toReview, fromSuccess];

    expect(parallelEdgeRoute(edges, toFix)).toEqual({ index: 0, count: 1 });
    expect(parallelEdgeRoute(edges, toReview)).toEqual({ index: 0, count: 1 });
    expect(parallelEdgeRoute(edges, fromSuccess)).toEqual({ index: 0, count: 1 });
  });
});

describe("parallelLabelOffset", () => {
  test("a lone edge keeps its label centred on the path", () => {
    expect(parallelLabelOffset(0, 1)).toBe(0);
  });

  test("two siblings stagger their labels symmetrically a full step apart", () => {
    expect(parallelLabelOffset(0, 2)).toBe(-PARALLEL_LABEL_STAGGER / 2);
    expect(parallelLabelOffset(1, 2)).toBe(PARALLEL_LABEL_STAGGER / 2);
    expect(parallelLabelOffset(1, 2) - parallelLabelOffset(0, 2)).toBe(PARALLEL_LABEL_STAGGER);
  });

  test("three siblings keep the middle label centred and the outer two a step out", () => {
    // The odd-count edge case: the centre label stays on the path midpoint while
    // its neighbours stagger above and below it.
    expect(parallelLabelOffset(0, 3)).toBe(-PARALLEL_LABEL_STAGGER);
    expect(parallelLabelOffset(1, 3)).toBe(0);
    expect(parallelLabelOffset(2, 3)).toBe(PARALLEL_LABEL_STAGGER);
  });
});

describe("buildEdge", () => {
  test("routes every edge through the custom loop edge type", () => {
    const built = buildEdge({
      source: "a",
      target: "b",
      edgeType: EdgeType.Custom,
      name: "Escalate",
      maxTraversals: null,
      sourceHandle: "respond",
      targetHandle: "target-handle",
    });
    expect(built.type).toBe(LOOP_EDGE_TYPE);
  });

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

describe("appendEdge", () => {
  test("keeps a sibling edge that shares the same connectors as an existing one", () => {
    // The reviewer's case: a second custom edge from the same handle into the
    // same target node. React Flow's addEdge would drop this as a duplicate
    // (same source/target/handles), preventing the staggered parallel edges from
    // ever being created; appendEdge keeps it.
    const config = {
      source: "pr",
      target: "cleanup",
      edgeType: EdgeType.Custom,
      maxTraversals: null,
      sourceHandle: "respond",
      targetHandle: "target-handle",
    };
    const onMerged = buildEdge({ ...config, name: "on_merged" });
    const onRejected = buildEdge({ ...config, name: "on_rejected" });

    const result = appendEdge(onRejected, [onMerged]);
    expect(result).toHaveLength(2);
    expect(result).toContain(onMerged);
    expect(result).toContain(onRejected);
    // Sharing their route, the two are now siblings that fan apart on render.
    expect(parallelEdgeRoute(result, onRejected).count).toBe(2);
  });

  test("does not mutate the original edges array", () => {
    const edges: Edge[] = [];
    const built = buildEdge({
      source: "a",
      target: "b",
      edgeType: EdgeType.OnSuccess,
      name: null,
      maxTraversals: null,
      sourceHandle: "success",
      targetHandle: "target-handle",
    });
    expect(appendEdge(built, edges)).toEqual([built]);
    expect(edges).toHaveLength(0);
  });
});
