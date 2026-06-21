import type { Edge, Node } from "@xyflow/react";
import { AiMatchRule, EdgeType, NodeType } from "../types";

// Every loop edge renders through the custom LoopEdgeComponent (registered under
// this type) so siblings that share one source/target route can fan apart.
export const LOOP_EDGE_TYPE = "loopEdge";

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

export function buildEdge(config: EdgeConfig): Edge {
  const name = config.edgeType === EdgeType.Custom ? (config.name ?? null) : null;

  return {
    id: `e-${Date.now()}`,
    source: config.source,
    target: config.target,
    sourceHandle: config.sourceHandle,
    targetHandle: config.targetHandle,
    type: LOOP_EDGE_TYPE,
    animated: config.edgeType === EdgeType.OnSuccess,
    data: { edgeType: config.edgeType, name },
    style: edgeVisualStyle(config.edgeType),
    label: edgeLabelFor(config.edgeType, name),
  };
}

// Perpendicular distance (flow units) between adjacent parallel lanes, applied
// to the control points of each edge's curve. A cubic reaches ~0.75x its control
// offset at the centre, so adjacent lanes sit ~0.75 * this value apart across the
// whole middle of the run — wide enough to read every label and click every edge.
// Earlier ~36px and ~64px peak-only attempts left the lanes too close to pick one.
export const PARALLEL_EDGE_SPREAD = 120;

// Fraction of the centre separation kept clickable: the cubic's control points
// sit a quarter and three-quarters along the chord so the lanes stay roughly
// parallel (not pinched to a single peak) across the central half of the run.
const PARALLEL_EDGE_CONTROL_T = 0.25;

// How far a cubic Bézier travels toward its control offset at the midpoint.
const CUBIC_MIDPOINT_FACTOR = 0.75;

// The transparent hit-area width React Flow draws under each parallel edge so
// any one in the fan can be clicked, not just the topmost path.
export const PARALLEL_EDGE_INTERACTION_WIDTH = 34;

/**
 * Where {@link edge} sits among the edges sharing its exact route — same
 * source/target node and the same source/target handle. A lone edge is
 * `{ index: 0, count: 1 }`; siblings get a stable index ordered by edge id so
 * every edge in the fan picks a different lane on every render.
 */
export function parallelEdgeRoute(edges: Edge[], edge: Edge): { index: number; count: number } {
  const siblings = edges
    .filter(
      (candidate) =>
        candidate.source === edge.source &&
        candidate.target === edge.target &&
        (candidate.sourceHandle ?? null) === (edge.sourceHandle ?? null) &&
        (candidate.targetHandle ?? null) === (edge.targetHandle ?? null),
    )
    .sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  return {
    index: Math.max(
      0,
      siblings.findIndex((candidate) => candidate.id === edge.id),
    ),
    count: siblings.length,
  };
}

/**
 * The perpendicular control offset (flow units) a parallel edge bows away from
 * the straight source→target chord. Lanes spread symmetrically around the chord
 * and widen with the sibling count, so two edges land at ±{@link
 * PARALLEL_EDGE_SPREAD}/2 and never share a path.
 */
export function parallelEdgeOffset(index: number, count: number): number {
  if (count <= 1) return 0;
  return (index - (count - 1) / 2) * PARALLEL_EDGE_SPREAD;
}

/**
 * A cubic Bézier from (sourceX, sourceY) to (targetX, targetY) whose two control
 * points are pushed `offset` flow units perpendicular to the chord — one a
 * quarter along, one three-quarters along. Keeping the endpoints on the handles
 * but flaring the middle gives each parallel edge a broad, near-parallel central
 * band (rather than a single crossing point) that stays separated enough to click
 * any one of them. The label rides the centre of that band.
 */
export function getParallelEdgePath(
  sourceX: number,
  sourceY: number,
  targetX: number,
  targetY: number,
  offset: number,
): { path: string; labelX: number; labelY: number } {
  const deltaX = targetX - sourceX;
  const deltaY = targetY - sourceY;
  const length = Math.hypot(deltaX, deltaY) || 1;
  const unitX = deltaX / length;
  const unitY = deltaY / length;
  const perpX = -unitY;
  const perpY = unitX;

  const along = length * PARALLEL_EDGE_CONTROL_T;
  const control1X = sourceX + unitX * along + perpX * offset;
  const control1Y = sourceY + unitY * along + perpY * offset;
  const control2X = targetX - unitX * along + perpX * offset;
  const control2Y = targetY - unitY * along + perpY * offset;

  const labelX = (sourceX + targetX) / 2 + perpX * offset * CUBIC_MIDPOINT_FACTOR;
  const labelY = (sourceY + targetY) / 2 + perpY * offset * CUBIC_MIDPOINT_FACTOR;
  return {
    path: `M ${sourceX},${sourceY} C ${control1X},${control1Y} ${control2X},${control2Y} ${targetX},${targetY}`,
    labelX,
    labelY,
  };
}
