import { createContext } from "react";
import type { Edge } from "@xyflow/react";

export type SelectLoopEdge = (edge: Edge) => void;

/**
 * Lets a rendered edge's label select its own edge directly. Parallel edges sit
 * close enough that a click landing on the canvas can hit whichever line is on
 * top rather than the one whose label was clicked; routing the click through the
 * label to this callback resolves it to that exact edge instead.
 */
export const LoopEdgeInteractionContext = createContext<SelectLoopEdge | null>(null);
