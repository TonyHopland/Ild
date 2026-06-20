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
// AI, PR and Condition nodes may additionally declare named custom edges
// (Condition declares its fixed "true"/"false" pair).
const customEdgeNodeTypes = new Set<NodeType>([
  NodeType.Human,
  NodeType.AI,
  NodeType.PR,
  NodeType.Condition,
]);

export function nodeAllowsCustomEdges(nodeType: NodeType): boolean {
  return customEdgeNodeTypes.has(nodeType);
}

// The seven reserved PR-node custom edges fired by the PR heartbeat poller, in
// priority order (highest first). Mirrors ILD.Core PrNodeEdges. Wiring one
// routes the run away from the parked PR node when that state is observed; an
// unwired edge never fires. There is NO fallback to on_success/on_failure — to
// reach a terminal/Cleanup path a template MUST wire on_merged / on_abandoned.
export const PR_RESERVED_EDGE_NAMES = [
  "on_rejected",
  "on_merge_conflict",
  "on_ci_failed",
  "on_approved",
  "on_ci_passed",
  "on_merged",
  "on_abandoned",
] as const;

/**
 * The custom-edge names a node declares, used to populate the "Which edge?"
 * dropdown when connecting from the custom handle. AI nodes derive them from
 * their match rules' edge names; Human nodes from their `customEdges` list; PR
 * nodes from their `customEdges` plus the seven reserved heartbeat edges so the
 * editor can always wire them.
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
  if (data?.type === NodeType.PR) {
    const names = (config.customEdges as string[] | undefined) ?? [];
    return collect([...PR_RESERVED_EDGE_NAMES, ...names]);
  }
  if (data?.type === NodeType.Human) {
    const names = (config.customEdges as string[] | undefined) ?? [];
    return collect(names);
  }
  return [];
}

/**
 * The custom-edge names actually wired out of a node, read from its connected
 * edges rather than its config. Seeded and migrated templates carry a connected
 * custom edge (e.g. "Respond") on Human/PR nodes without a matching
 * `customEdges` config entry, so the editor must union these in or the edge —
 * and the run-time button it produces — stays invisible in the settings panel.
 */
export function getConnectedCustomEdgeNames(nodeId: string, edges: Edge[]): string[] {
  const seen = new Set<string>();
  for (const edge of edges) {
    if (edge.source !== nodeId) continue;
    const data = edge.data as { edgeType?: EdgeType; name?: string | null };
    if (data?.edgeType !== EdgeType.Custom) continue;
    const name = data?.name?.trim();
    if (name) seen.add(name);
  }
  return [...seen];
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

// React Flow v12 dropped `pathOptions` from the generic `Edge` type; it only
// lives on the built-in smoothstep variant, which isn't exported. Re-declare it
// here so the smoothstep routing options below type-check.
type SmoothStepEdge = Edge & {
  pathOptions?: { borderRadius?: number; offset?: number };
};

export function buildEdge(config: EdgeConfig): SmoothStepEdge {
  const name = config.edgeType === EdgeType.Custom ? (config.name ?? null) : null;

  return {
    id: `e-${Date.now()}`,
    source: config.source,
    target: config.target,
    sourceHandle: config.sourceHandle,
    targetHandle: config.targetHandle,
    // Rounded orthogonal routing; offset pushes the stub clear of the node so
    // 180° turns don't clip it.
    type: "smoothstep",
    pathOptions: { borderRadius: 20, offset: 20 },
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
