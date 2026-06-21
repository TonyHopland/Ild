import { describe, expect, test } from "vite-plus/test";
import type { Edge } from "@xyflow/react";
import { templateToEdges, edgesToLoopNodeEdges } from "./loopGraphConverter";
import { LOOP_EDGE_TYPE } from "./edgeUtils";
import { EdgeType, type LoopTemplate } from "../types";

function template(edges: LoopTemplate["edges"]): LoopTemplate {
  return {
    id: "t",
    name: "t",
    description: "",
    version: 1,
    recoveryPolicy: "AutoResume" as LoopTemplate["recoveryPolicy"],
    nodes: [],
    edges,
    createdAt: "",
    updatedAt: "",
    isArchived: false,
  };
}

describe("loopGraphConverter custom edges", () => {
  test("templateToEdges carries the name through data and uses it as the label", () => {
    const [edge] = templateToEdges(
      template([
        {
          id: "e1",
          sourceNodeId: "a",
          targetNodeId: "b",
          edgeType: EdgeType.Custom,
          name: "Escalate",
          maxTraversals: null,
        },
      ]),
    );

    expect((edge.data as { name?: string }).name).toBe("Escalate");
    expect(edge.label).toBe("Escalate");
    // Reuses the existing top/respond handle as the single custom outlet.
    expect(edge.sourceHandle).toBe("respond");
  });

  test("two custom edges from one node to the same target keep distinct labels and a shared route", () => {
    const edges = templateToEdges(
      template([
        {
          id: "e-ci",
          sourceNodeId: "pr",
          targetNodeId: "fix",
          edgeType: EdgeType.Custom,
          name: "on_ci_failed",
          maxTraversals: null,
        },
        {
          id: "e-reject",
          sourceNodeId: "pr",
          targetNodeId: "fix",
          edgeType: EdgeType.Custom,
          name: "on_rejected",
          maxTraversals: null,
        },
      ]),
    );

    // Each edge keeps its own readable label rather than stacking one over the other.
    expect(edges.map((edge) => edge.label)).toEqual(["on_ci_failed", "on_rejected"]);
    // They share the same source/target handles, so the custom edge component
    // fans them apart by route.
    expect(edges.every((edge) => edge.type === LOOP_EDGE_TYPE)).toBe(true);
    expect(edges.every((edge) => edge.sourceHandle === "respond")).toBe(true);
    expect(edges.every((edge) => edge.targetHandle === "target-handle")).toBe(true);
  });

  test("a custom edge with no name falls back to a generic 'custom' label", () => {
    const [edge] = templateToEdges(
      template([
        {
          id: "e1",
          sourceNodeId: "a",
          targetNodeId: "b",
          edgeType: EdgeType.Custom,
          name: null,
          maxTraversals: null,
        },
      ]),
    );
    expect(edge.label).toBe("custom");
  });

  test("edgesToLoopNodeEdges round-trips the custom name and nulls it for non-custom edges", () => {
    const result = edgesToLoopNodeEdges([
      {
        id: "e1",
        source: "a",
        target: "b",
        data: { edgeType: EdgeType.Custom, name: "Respond" },
      } as Edge,
      {
        id: "e2",
        source: "a",
        target: "c",
        data: { edgeType: EdgeType.OnSuccess, name: "leftover" },
      } as Edge,
    ]);

    expect(result[0].name).toBe("Respond");
    expect(result[1].name).toBeNull();
  });
});
