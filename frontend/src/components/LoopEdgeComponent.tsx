import { useCallback, useContext, useState } from "react";
import {
  BaseEdge,
  EdgeLabelRenderer,
  getSmoothStepPath,
  useReactFlow,
  useStore,
  type EdgeProps,
} from "@xyflow/react";
import { parallelEdgeRoute, parallelLabelOffset } from "../utils/edgeUtils";
import { LoopEdgeInteractionContext } from "./loopEdgeInteraction";

// Mirrors the rounded orthogonal routing the editor used before parallel edges
// existed, so a lone edge between two nodes looks exactly as it always has.
const SMOOTHSTEP_BORDER_RADIUS = 20;
const SMOOTHSTEP_OFFSET = 20;

/**
 * Renders a loop edge. Every edge — lone or one of several sharing the same
 * source/target route (e.g. several PR custom edges into one node) — draws the
 * same smooth-step path, so each connects cleanly at its handles. Siblings are
 * told apart by staggering their labels vertically rather than pulling the lines
 * onto distant lanes. The label is itself the click target — selecting it picks
 * that exact edge, so the overlapping lines can't steal the click.
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
  // Highlight the edge under the cursor — a thicker, more vibrant line makes it
  // obvious which two nodes the hovered connection joins, even where several
  // siblings share one route. React Flow's transparent interaction path (the fat
  // hit area BaseEdge draws beneath the visible line) gives us a forgiving target,
  // and its pointer events bubble up to the wrapping <g>.
  const [hovered, setHovered] = useState(false);
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

  const [edgePath, pathLabelX, pathLabelY] = getSmoothStepPath({
    sourceX,
    sourceY,
    sourcePosition,
    targetX,
    targetY,
    targetPosition,
    borderRadius: SMOOTHSTEP_BORDER_RADIUS,
    offset: SMOOTHSTEP_OFFSET,
  });
  // Siblings share this path exactly, so stagger each label off the midpoint to
  // keep the overlapping names readable.
  const labelX = pathLabelX;
  const labelY = pathLabelY + parallelLabelOffset(index, count);

  // On hover, thicken the line and brighten its colour. `brightness` works for
  // any edge colour (success/failure/custom) without hard-coding a second palette.
  const hoverStyle = hovered ? { strokeWidth: 3, filter: "brightness(1.4)" } : undefined;

  return (
    <g onMouseEnter={() => setHovered(true)} onMouseLeave={() => setHovered(false)}>
      <BaseEdge id={id} path={edgePath} markerEnd={markerEnd} style={{ ...style, ...hoverStyle }} />
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
    </g>
  );
}
