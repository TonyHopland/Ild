import { useCallback, useContext } from "react";
import {
  BaseEdge,
  EdgeLabelRenderer,
  getSmoothStepPath,
  useReactFlow,
  useStore,
  type EdgeProps,
} from "@xyflow/react";
import {
  getBowedEdgePath,
  parallelEdgeOffset,
  parallelEdgeRoute,
  PARALLEL_EDGE_INTERACTION_WIDTH,
} from "../utils/edgeUtils";
import { LoopEdgeInteractionContext } from "./loopEdgeInteraction";

// Mirrors the rounded orthogonal routing the editor used before parallel edges
// existed, so a lone edge between two nodes looks exactly as it always has.
const SMOOTHSTEP_BORDER_RADIUS = 20;
const SMOOTHSTEP_OFFSET = 20;

/**
 * Renders a loop edge. A lone edge keeps the original smooth-step path. When two
 * or more edges run between the same pair of nodes (e.g. several PR custom edges
 * into one node, or a success and a custom edge into the same target) they each
 * bow gently apart — endpoints still anchored on the nodes — so every label is
 * offset onto its own spot and stays readable. The label is itself the click
 * target — selecting it picks that exact edge, so neighbouring lines can't steal
 * the click.
 */
export default function LoopEdgeComponent({
  id,
  sourceX,
  sourceY,
  targetX,
  targetY,
  sourcePosition,
  targetPosition,
  markerEnd,
  style,
  label,
}: EdgeProps) {
  const selectEdge = useContext(LoopEdgeInteractionContext);
  const { getEdge } = useReactFlow();
  const onLabelClick = useCallback(
    (event: React.MouseEvent) => {
      // Stop the canvas from also handling the click (which would clear the
      // selection), then select this label's own edge by id.
      event.stopPropagation();
      const self = getEdge(id);
      if (self && selectEdge) selectEdge(self);
    },
    [getEdge, id, selectEdge],
  );
  // Read this edge's lane (index) and how many edges share its route straight
  // from the store so the fan re-balances as siblings are added or removed.
  const route = useStore(
    useCallback(
      (store) => {
        const self = store.edges.find((edge) => edge.id === id);
        if (!self) return "0|1";
        const { index, count } = parallelEdgeRoute(store.edges, self);
        return `${index}|${count}`;
      },
      [id],
    ),
  );
  const [index, count] = route.split("|").map(Number);

  let edgePath: string;
  let labelX: number;
  let labelY: number;
  if (count <= 1) {
    [edgePath, labelX, labelY] = getSmoothStepPath({
      sourceX,
      sourceY,
      sourcePosition,
      targetX,
      targetY,
      targetPosition,
      borderRadius: SMOOTHSTEP_BORDER_RADIUS,
      offset: SMOOTHSTEP_OFFSET,
    });
  } else {
    const bowed = getBowedEdgePath(
      sourceX,
      sourceY,
      targetX,
      targetY,
      parallelEdgeOffset(index, count),
    );
    edgePath = bowed.path;
    labelX = bowed.labelX;
    labelY = bowed.labelY;
  }

  return (
    <>
      <BaseEdge
        id={id}
        path={edgePath}
        markerEnd={markerEnd}
        style={style}
        interactionWidth={count > 1 ? PARALLEL_EDGE_INTERACTION_WIDTH : undefined}
      />
      {label && (
        <EdgeLabelRenderer>
          <div
            className="nodrag nopan loop-edge-label"
            role="button"
            tabIndex={0}
            onClick={onLabelClick}
            style={{
              position: "absolute",
              transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)`,
              // The label is the edge's reliable click target: parallel lines run
              // close enough that a canvas click can hit the wrong one, so make the
              // label itself capture the click and select its own edge.
              pointerEvents: "all",
              cursor: "pointer",
              fontSize: "0.7rem",
              color: "#a0a0b0",
              background: "#1e1e30",
              padding: "2px 4px",
              borderRadius: "4px",
            }}
          >
            {label}
          </div>
        </EdgeLabelRenderer>
      )}
    </>
  );
}
