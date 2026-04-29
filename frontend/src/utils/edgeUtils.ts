import type { Edge } from "@xyflow/react";
import { EdgeType } from "../types";

export interface EdgeConfig {
  source: string;
  target: string;
  edgeType: EdgeType;
  maxTraversals: number | null;
}

export interface EdgeConstraintResult {
  allowed: boolean;
  error?: string;
  suggestedType?: EdgeType;
}

export function checkEdgeConstraints(
  sourceId: string,
  existingEdges: Edge[],
): EdgeConstraintResult {
  const hasOnSuccess = existingEdges.some(
    (e) => e.source === sourceId && e.data?.edgeType === EdgeType.OnSuccess,
  );
  const hasOnFailure = existingEdges.some(
    (e) => e.source === sourceId && e.data?.edgeType === EdgeType.OnFailure,
  );

  if (hasOnSuccess && hasOnFailure) {
    return {
      allowed: false,
      error: "Source node already has both OnSuccess and OnFailure edges",
    };
  }

  if (hasOnSuccess) {
    return { allowed: true, suggestedType: EdgeType.OnFailure };
  }

  if (hasOnFailure) {
    return { allowed: true, suggestedType: EdgeType.OnSuccess };
  }

  return { allowed: true, suggestedType: EdgeType.OnSuccess };
}

export function buildEdge(config: EdgeConfig): Edge {
  return {
    id: `e-${Date.now()}`,
    source: config.source,
    target: config.target,
    animated: config.edgeType === EdgeType.OnSuccess,
    data: { edgeType: config.edgeType },
    style: {
      stroke: config.edgeType === EdgeType.OnSuccess ? "#10b981" : "#ef4444",
      strokeDasharray: config.edgeType === EdgeType.OnFailure ? "8 4" : undefined,
    },
    label: config.edgeType === EdgeType.OnSuccess ? "success" : "failure",
    labelStyle: { fill: "#a0a0b0", fontSize: "0.7rem" },
    labelBgStyle: { fill: "#1e1e30" },
    labelBgPadding: [4, 2] as [number, number],
    labelBgBorderRadius: 4,
  };
}
