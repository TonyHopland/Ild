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

// Perpendicular distance (flow units) between adjacent parallel lanes. Because
// each lane shifts BOTH of its endpoints (see getParallelEdgePath), siblings stay
// exactly this far apart along their whole length — not just at a midpoint — so
// the gap is the real, constant separation a user clicks into. Earlier attempts
// that only bowed the middle (36px, then 64px, then a 120px peak) still pinched
// back together near the nodes; this keeps them apart end to end.
export const PARALLEL_EDGE_SPREAD = 120;

// The transparent hit-area width React Flow draws under each parallel edge so
// any one in the fan can be clicked, not just the topmost path.
export const PARALLEL_EDGE_INTERACTION_WIDTH = 34;

/**
 * Where {@link edge} sits among the edges running between its own pair of nodes —
 * same source node and same target node, regardless of which handle each leaves
 * or enters. Grouping by handle is too narrow: a node's success/failure/custom
 * outlets all converge on one target's single inlet, so an OnSuccess and a Custom
 * edge into the same node still overlap even though their source handles differ.
 * A lone edge is `{ index: 0, count: 1 }`; siblings get a stable index ordered by
 * edge id so every edge in the fan picks a different lane on every render.
 */
export function parallelEdgeRoute(edges: Edge[], edge: Edge): { index: number; count: number } {
  const siblings = edges
    .filter((candidate) => candidate.source === edge.source && candidate.target === edge.target)
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
 * The perpendicular offset (flow units) a parallel edge is shifted onto its own
 * lane, away from the straight source→target chord. Lanes spread symmetrically
 * around the chord and widen with the sibling count, so two edges run at ±{@link
 * PARALLEL_EDGE_SPREAD}/2 and never share a path.
 */
export function parallelEdgeOffset(index: number, count: number): number {
  if (count <= 1) return 0;
  return (index - (count - 1) / 2) * PARALLEL_EDGE_SPREAD;
}

/**
 * Routes a parallel edge along its own lane: BOTH endpoints are shifted `offset`
 * flow units perpendicular to the source→target chord, giving each edge a
 * distinct departure and landing point. The result is a straight track that runs
 * a constant `offset` clear of its siblings end to end — so they never re-converge
 * and overlap near the nodes the way a midpoint-only bow did. The label rides the
 * centre of the shifted track.
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
  const perpX = (-deltaY / length) * offset;
  const perpY = (deltaX / length) * offset;

  const startX = sourceX + perpX;
  const startY = sourceY + perpY;
  const endX = targetX + perpX;
  const endY = targetY + perpY;

  return {
    path: `M ${startX},${startY} L ${endX},${endY}`,
    labelX: (startX + endX) / 2,
    labelY: (startY + endY) / 2,
  };
}
