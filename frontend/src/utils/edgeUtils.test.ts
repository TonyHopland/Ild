import { describe, expect, test } from "vite-plus/test";
import type { Edge, Node } from "@xyflow/react";
import {
  checkEdgeConstraints,
  getCustomEdgeNames,
  getConnectedCustomEdgeNames,
  buildEdge,
  parallelEdgeRoute,
  parallelEdgeOffset,
  getBowedEdgePath,
  PARALLEL_EDGE_GAP,
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

  test("edges between the same node pair are siblings even when their handles differ", () => {
    // The seed-template case: an OnSuccess edge (success handle) and a Custom edge
    // (respond handle) both run into the same target node and overlap, because
    // every outlet converges on the target's single inlet.
    const fromSuccess = routedEdge("e1", "pr", "fix", "success", "target-handle");
    const fromRespond = routedEdge("e2", "pr", "fix", "respond", "target-handle");
    const edges = [fromSuccess, fromRespond];

    expect(parallelEdgeRoute(edges, fromSuccess).count).toBe(2);
    expect(parallelEdgeRoute(edges, fromRespond).count).toBe(2);
    // Ordered by id, so each occupies its own lane deterministically.
    expect(parallelEdgeRoute(edges, fromSuccess).index).toBe(0);
    expect(parallelEdgeRoute(edges, fromRespond).index).toBe(1);
  });

  test("edges to different targets are not siblings", () => {
    const toFix = routedEdge("e1", "pr", "fix", "respond", "target-handle");
    const toReview = routedEdge("e2", "pr", "review", "respond", "target-handle");
    const edges = [toFix, toReview];

    expect(parallelEdgeRoute(edges, toFix)).toEqual({ index: 0, count: 1 });
    expect(parallelEdgeRoute(edges, toReview)).toEqual({ index: 0, count: 1 });
  });
});

describe("parallelEdgeOffset", () => {
  test("a lone edge stays on the chord", () => {
    expect(parallelEdgeOffset(0, 1)).toBe(0);
  });

  test("two siblings bow symmetrically a full gap apart", () => {
    expect(parallelEdgeOffset(0, 2)).toBe(-PARALLEL_EDGE_GAP / 2);
    expect(parallelEdgeOffset(1, 2)).toBe(PARALLEL_EDGE_GAP / 2);
    expect(parallelEdgeOffset(1, 2) - parallelEdgeOffset(0, 2)).toBe(PARALLEL_EDGE_GAP);
  });

  test("three siblings keep the middle on the chord and the outer two a gap out", () => {
    expect(parallelEdgeOffset(0, 3)).toBe(-PARALLEL_EDGE_GAP);
    expect(parallelEdgeOffset(1, 3)).toBe(0);
    expect(parallelEdgeOffset(2, 3)).toBe(PARALLEL_EDGE_GAP);
  });
});

describe("getBowedEdgePath", () => {
  test("a zero offset rides the straight chord with the label at its midpoint", () => {
    const { path, labelX, labelY } = getBowedEdgePath(0, 0, 100, 0, 0);
    // No bow: control point sits on the chord midpoint and the label with it.
    expect(path).toBe("M 0,0 Q 50,0 100,0");
    expect(labelX).toBe(50);
    expect(labelY).toBe(0);
  });

  test("keeps both endpoints on the handles and only bows the middle out", () => {
    // A +50 offset bows the curve to its peak 50 units off the chord while the
    // start and end stay pinned to the real handles — so the edge never floats
    // free of the nodes. The control point is twice the peak offset.
    const { path, labelX, labelY } = getBowedEdgePath(0, 0, 100, 0, 50);
    expect(path).toBe("M 0,0 Q 50,100 100,0");
    expect(labelX).toBe(50);
    expect(labelY).toBe(50);
  });

  test("opposite siblings bow to peaks a full gap apart while sharing endpoints", () => {
    const up = getBowedEdgePath(0, 0, 100, 0, -PARALLEL_EDGE_GAP / 2);
    const down = getBowedEdgePath(0, 0, 100, 0, PARALLEL_EDGE_GAP / 2);

    // Their labels (at the bowed peaks) sit a full gap apart so each is clickable,
    // yet both curves start at 0,0 and end at 100,0 — anchored to the nodes.
    expect(down.labelY - up.labelY).toBe(PARALLEL_EDGE_GAP);
    expect(up.path).toBe(`M 0,0 Q 50,${-PARALLEL_EDGE_GAP} 100,0`);
    expect(down.path).toBe(`M 0,0 Q 50,${PARALLEL_EDGE_GAP} 100,0`);
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
