import type { Edge, Node } from "@xyflow/react";
import { AiMatchRule, EdgeType, NodeType } from "../types";

export interface EdgeConfig {
  source: string;
  target: string;
  edgeType: EdgeType;
  name?: string | null;
  maxTraversals: number | null;
  sourceHandle: string;
  targetHandle: string;
}

export interface EdgeConstraintResult {
  allowed: boolean;
  error?: string;
}

// Every node (except the Cleanup sink) routes success and failure. Only Human,
// AI and PR nodes may additionally declare named custom edges.
const customEdgeNodeTypes = new Set<NodeType>([NodeType.Human, NodeType.AI, NodeType.PR]);

export function nodeAllowsCustomEdges(nodeType: NodeType): boolean {
  return customEdgeNodeTypes.has(nodeType);
}

/**
 * The custom-edge names a node declares, used to populate the "Which edge?"
 * dropdown when connecting from the custom handle. AI nodes derive them from
 * their match rules' edge names; Human and PR nodes from their `customEdges`
 * list.
 */
export function getCustomEdgeNames(node: Node | undefined | null): string[] {
  if (!node) return [];
  const data = node.data as { type?: NodeType; config?: Record<string, unknown> };
  const config = data?.config ?? {};
  const collect = (values: (string | undefined)[]): string[] => {
    const seen = new Set<string>();
    for (const value of values) {
      const trimmed = value?.trim();
      if (trimmed) seen.add(trimmed);
    }
    return [...seen];
  };

  if (data?.type === NodeType.AI) {
    const rules = (config.matchRules as AiMatchRule[] | undefined) ?? [];
    return collect(rules.map((rule) => rule?.edgeName));
  }
  if (data?.type === NodeType.Human || data?.type === NodeType.PR) {
    const names = (config.customEdges as string[] | undefined) ?? [];
    return collect(names);
  }
  return [];
}

/**
 * Validates that a node of {@link sourceNodeType} may gain an outgoing edge of
 * {@link edgeType}. Default/fallback edges are single per node; custom edges are
 * allowed in any number on Human/AI/PR (per-name uniqueness is enforced at
 * confirm time, once the user has picked a name).
 */
export function checkEdgeConstraints(
  sourceId: string,
  sourceNodeType: NodeType,
  edgeType: EdgeType,
  existingEdges: Edge[],
): EdgeConstraintResult {
  if (sourceNodeType === NodeType.Cleanup) {
    return { allowed: false, error: "Cleanup nodes cannot have outgoing edges" };
  }
  if (edgeType === EdgeType.Custom) {
    if (!nodeAllowsCustomEdges(sourceNodeType)) {
      return {
        allowed: false,
        error: "Only Human, AI and PR nodes can have custom edges",
      };
    }
    return { allowed: true };
  }

  const exists = existingEdges.some(
    (edge) => edge.source === sourceId && edge.data?.edgeType === edgeType,
  );
  if (exists) {
    return { allowed: false, error: "This edge type is already connected from this node" };
  }
  return { allowed: true };
}

function edgeVisualStyle(edgeType: EdgeType) {
  if (edgeType === EdgeType.OnSuccess) return { stroke: "#10b981" as const };
  if (edgeType === EdgeType.OnFailure)
    return { stroke: "#ef4444" as const, strokeDasharray: "8 4" as const };
  return { stroke: "#f59e0b" as const, strokeDasharray: "4 4" as const };
}

function edgeLabelFor(edgeType: EdgeType, name?: string | null): string {
  if (edgeType === EdgeType.OnSuccess) return "success";
  if (edgeType === EdgeType.OnFailure) return "failure";
  // Custom edges read by their name so overlapping outlets stay distinguishable.
  return name?.trim() || "custom";
}

export function buildEdge(config: EdgeConfig): Edge {
  const name = config.edgeType === EdgeType.Custom ? (config.name ?? null) : null;

  return {
    id: `e-${Date.now()}`,
    source: config.source,
    target: config.target,
    sourceHandle: config.sourceHandle,
    targetHandle: config.targetHandle,
    animated: config.edgeType === EdgeType.OnSuccess,
    data: { edgeType: config.edgeType, name },
    style: edgeVisualStyle(config.edgeType),
    label: edgeLabelFor(config.edgeType, name),
    labelStyle: { fill: "#a0a0b0", fontSize: "0.7rem" },
    labelBgStyle: { fill: "#1e1e30" },
    labelBgPadding: [4, 2] as [number, number],
    labelBgBorderRadius: 4,
  };
}
