import type { Edge } from "@xyflow/react";
import { EdgeType, NodeType } from "../types";

export interface EdgeConfig {
  source: string;
  target: string;
  edgeType: EdgeType;
  maxTraversals: number | null;
  sourceHandle: string;
  targetHandle: string;
}

export interface EdgeConstraintResult {
  allowed: boolean;
  error?: string;
  suggestedType?: EdgeType;
}

const allowedEdgeTypesByNodeType: Record<NodeType, EdgeType[]> = {
  [NodeType.Start]: [EdgeType.OnSuccess, EdgeType.OnFailure],
  [NodeType.Cmd]: [EdgeType.OnSuccess, EdgeType.OnFailure],
  [NodeType.AI]: [EdgeType.OnSuccess, EdgeType.OnFailure],
  [NodeType.Human]: [EdgeType.OnSuccess, EdgeType.OnFailure, EdgeType.OnRespond],
  [NodeType.Prompt]: [EdgeType.OnSuccess, EdgeType.OnFailure],
  [NodeType.PR]: [EdgeType.OnSuccess, EdgeType.OnFailure, EdgeType.OnRespond],
  [NodeType.Cleanup]: [EdgeType.OnSuccess, EdgeType.OnFailure],
};

export function checkEdgeConstraints(
  sourceId: string,
  sourceNodeType: NodeType,
  existingEdges: Edge[],
): EdgeConstraintResult {
  const allowed = allowedEdgeTypesByNodeType[sourceNodeType] ?? [
    EdgeType.OnSuccess,
    EdgeType.OnFailure,
  ];
  const hasOnSuccess = existingEdges.some(
    (e) => e.source === sourceId && e.data?.edgeType === EdgeType.OnSuccess,
  );
  const hasOnFailure = existingEdges.some(
    (e) => e.source === sourceId && e.data?.edgeType === EdgeType.OnFailure,
  );
  const hasOnRespond = existingEdges.some(
    (e) => e.source === sourceId && e.data?.edgeType === EdgeType.OnRespond,
  );

  const existingTypes = new Set<EdgeType>();
  if (hasOnSuccess) existingTypes.add(EdgeType.OnSuccess);
  if (hasOnFailure) existingTypes.add(EdgeType.OnFailure);
  if (hasOnRespond) existingTypes.add(EdgeType.OnRespond);

  const remaining = allowed.filter((t) => !existingTypes.has(t));
  if (remaining.length === 0) {
    return {
      allowed: false,
      error: "Source node already has all allowed edge types",
    };
  }

  return { allowed: true, suggestedType: remaining[0] };
}

export function buildEdge(config: EdgeConfig): Edge {
  const edgeStyle =
    config.edgeType === EdgeType.OnSuccess
      ? { stroke: "#10b981" as const }
      : config.edgeType === EdgeType.OnFailure
        ? { stroke: "#ef4444" as const, strokeDasharray: "8 4" as const }
        : { stroke: "#f59e0b" as const, strokeDasharray: "4 4" as const };

  const edgeLabel =
    config.edgeType === EdgeType.OnSuccess
      ? "success"
      : config.edgeType === EdgeType.OnFailure
        ? "failure"
        : "respond";

  return {
    id: `e-${Date.now()}`,
    source: config.source,
    target: config.target,
    sourceHandle: config.sourceHandle,
    targetHandle: config.targetHandle,
    animated: config.edgeType === EdgeType.OnSuccess,
    data: { edgeType: config.edgeType },
    style: edgeStyle,
    label: edgeLabel,
    labelStyle: { fill: "#a0a0b0", fontSize: "0.7rem" },
    labelBgStyle: { fill: "#1e1e30" },
    labelBgPadding: [4, 2] as [number, number],
    labelBgBorderRadius: 4,
  };
}
