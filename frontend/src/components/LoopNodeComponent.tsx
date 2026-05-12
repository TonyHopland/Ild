import { Handle, Position, type NodeProps } from "@xyflow/react";
import { NodeType } from "../types";

const nodeStyles: Record<string, { bg: string; border: string; icon: string }> = {
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
  [NodeType.Cleanup]: {
    bg: "#4c0519",
    border: "#ef4444",
    icon: "\uD83E\uDDD9",
  },
};

const handleStyles = {
  success: { background: "#10b981", borderColor: "#059669" },
  fail: { background: "#ef4444", borderColor: "#dc2626" },
  respond: { background: "#f59e0b", borderColor: "#d97706" },
};

export default function LoopNodeComponent({ data }: NodeProps) {
  const nodeData = data as { label: string; type: string };
  const style = nodeStyles[nodeData.type] || nodeStyles[NodeType.Cmd];
  const isHuman = nodeData.type === NodeType.Human;

  return (
    <div
      className="loop-node"
      style={{
        background: style.bg,
        border: `2px solid ${style.border}`,
        borderRadius: "8px",
        padding: "12px 16px",
        minWidth: "140px",
        color: "#e0e0e0",
      }}
    >
      <Handle
        type="target"
        position={Position.Left}
        id="target-handle"
        data-testid="target-handle"
        style={{ background: "#555", borderColor: "#777" }}
      />
      <Handle
        type="source"
        position={Position.Right}
        id="success"
        data-testid="source-handle-success"
        className="handle-success"
        style={handleStyles.success}
      />
      <Handle
        type="source"
        position={Position.Bottom}
        id="fail"
        data-testid="source-handle-fail"
        className="handle-fail"
        style={handleStyles.fail}
      />
      {isHuman && (
        <Handle
          type="source"
          position={Position.Top}
          id="respond"
          data-testid="source-handle-respond"
          className="handle-respond"
          style={handleStyles.respond}
        />
      )}
      <div
        className="loop-node-type"
        style={{
          fontSize: "0.7rem",
          fontWeight: 600,
          color: style.border,
          marginBottom: "4px",
          display: "flex",
          alignItems: "center",
          gap: "4px",
        }}
      >
        <span>{style.icon}</span>
        <span>{nodeData.type}</span>
      </div>
      <div
        className="loop-node-label"
        style={{
          fontSize: "0.85rem",
          fontWeight: 500,
        }}
      >
        {nodeData.label}
      </div>
    </div>
  );
}
