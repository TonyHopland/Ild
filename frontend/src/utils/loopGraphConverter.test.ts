import { describe, expect, test } from "vite-plus/test";
import type { Edge } from "@xyflow/react";
import { templateToEdges, edgesToLoopNodeEdges } from "./loopGraphConverter";
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
