import { Handle, Position, type NodeProps } from "@xyflow/react";
import { NodeType } from "../types";
import { nodeHasNamedOutputs } from "../utils/edgeUtils";
import { nodeStyleOf } from "../utils/nodeStyles";

const handleStyles = {
  success: { background: "#10b981", borderColor: "#059669" },
  fail: { background: "#ef4444", borderColor: "#dc2626" },
  respond: { background: "#f59e0b", borderColor: "#d97706" },
};

export default function LoopNodeComponent({ data }: NodeProps) {
  const nodeData = data as { label: string; type: string };
  const style = nodeStyleOf(nodeData.type);
  // The top handle is the single "custom" outlet; the Custom edge of every named
  // output the node declares leaves it. Human, AI, PR and Condition nodes have
  // named outputs (a Condition switch routes its cases and default through it).
  const hasCustomHandle = nodeHasNamedOutputs(nodeData.type as NodeType);
  // A Condition node has no default success outlet — it routes only through its
  // named outputs (its cases and default, plus the fail handle for an
  // evaluation error).
  const isCondition = nodeData.type === NodeType.Condition;

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
      {!isCondition && (
        <Handle
          type="source"
          position={Position.Right}
          id="success"
          data-testid="source-handle-success"
          className="handle-success"
          style={handleStyles.success}
        />
      )}
      <Handle
        type="source"
        position={Position.Bottom}
        id="fail"
        data-testid="source-handle-fail"
        className="handle-fail"
        style={handleStyles.fail}
      />
      {hasCustomHandle && (
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
