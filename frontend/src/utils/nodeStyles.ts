import { NodeType } from "../types";

export interface NodeStyle {
  bg: string;
  border: string;
  icon: string;
}

const nodeStyles: Record<string, NodeStyle> = {
  [NodeType.Start]: {
    bg: "#064e3b",
    border: "#10b981",
    icon: "\u25B6",
  },
  [NodeType.Cmd]: {
    bg: "#1e1b4b",
    border: "#6366f1",
    icon: "\u2699",
  },
  [NodeType.AI]: {
    bg: "#1c1917",
    border: "#f59e0b",
    icon: "\uD83E\uDD16",
  },
  [NodeType.Human]: {
    bg: "#1e1b4b",
    border: "#a855f7",
    icon: "\uD83D\uDC64",
  },
  [NodeType.Prompt]: {
    bg: "#172554",
    border: "#38bdf8",
    icon: "\u270E",
  },
  [NodeType.PR]: {
    bg: "#0c4a6e",
    border: "#0ea5e9",
    icon: "\uD83D\uDD01",
  },
  [NodeType.Condition]: {
    bg: "#042f2e",
    border: "#14b8a6",
    icon: "\u25C7",
  },
  [NodeType.Cleanup]: {
    bg: "#4c0519",
    border: "#ef4444",
    icon: "\uD83E\uDDD9",
  },
};

/** How a node of `type` looks on the canvas; an unknown type looks like a Cmd node. */
export function nodeStyleOf(type: string): NodeStyle {
  return nodeStyles[type] ?? nodeStyles[NodeType.Cmd];
}

/** The canvas icon of a node of `type`; undefined for a type the canvas does not know. */
export function nodeIconOf(type: string): string | undefined {
  return nodeStyles[type]?.icon;
}
