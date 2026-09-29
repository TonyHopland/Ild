import { describe, expect, test } from "vite-plus/test";
import type { Edge } from "@xyflow/react";
import {
  checkEdgeConstraints,
  buildEdge,
  appendEdge,
  parallelEdgeRoute,
  parallelLabelOffset,
  PARALLEL_LABEL_STAGGER,
  LOOP_EDGE_TYPE,
} from "./edgeUtils";
import { EdgeType, NodeType } from "../types";

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

describe("checkEdgeConstraints", () => {
  test("allows any number of custom edges on an AI node", () => {
    const existing = [edge("a", EdgeType.Custom, "Reject"), edge("a", EdgeType.Custom, "Escalate")];
    const result = checkEdgeConstraints("a", NodeType.AI, EdgeType.Custom, existing);
    expect(result.allowed).toBe(true);
  });

  test("rejects custom edges from a node type that cannot have them", () => {
    const result = checkEdgeConstraints("c", NodeType.Cmd, EdgeType.Custom, []);
    expect(result.allowed).toBe(false);
    expect(result.error).toBeTruthy();
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
      sourceHandle: "success",
      targetHandle: "target-handle",
    });
    expect(appendEdge(built, edges)).toEqual([built]);
    expect(edges).toHaveLength(0);
  });
});
