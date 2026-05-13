import type { Edge, Node } from "@xyflow/react";
import { EdgeType } from "../../../types";

interface EdgePanelsProps {
  pendingConnection: boolean;
  edgeType: EdgeType;
  edgeMaxTraversals: string;
  edgeError: string | null;
  showEdgeDeletePanel: boolean;
  selectedEdge: Edge | null;
  nodes: Node[];
  onEdgeMaxTraversalsChange: (value: string) => void;
  onConfirmEdge: () => void;
  onCancelEdge: () => void;
  onDeleteEdge: () => void;
}

function edgeTypeLabel(edgeType: EdgeType | undefined): string {
  if (edgeType === EdgeType.OnFailure) return "failure";
  if (edgeType === EdgeType.OnRespond) return "respond";
  return "success";
}

function edgeTypeClassName(edgeType: EdgeType | undefined): string {
  if (edgeType === EdgeType.OnFailure) return "edge-type-dot--failure";
  if (edgeType === EdgeType.OnRespond) return "edge-type-dot--respond";
  return "edge-type-dot--success";
}

function nodeLabel(nodes: Node[], nodeId: string): string {
  const matchingNode = nodes.find((node) => node.id === nodeId);
  return ((matchingNode?.data as { label?: string } | undefined)?.label ?? nodeId) as string;
}

export function EdgePanels({
  pendingConnection,
  edgeType,
  edgeMaxTraversals,
  edgeError,
  showEdgeDeletePanel,
  selectedEdge,
  nodes,
  onEdgeMaxTraversalsChange,
  onConfirmEdge,
  onCancelEdge,
  onDeleteEdge,
}: EdgePanelsProps) {
  const selectedEdgeType = selectedEdge?.data?.edgeType as EdgeType | undefined;

  return (
    <>
      {pendingConnection && (
        <div className="edge-config-panel react-flow__panel react-flow__panel-bottom-center">
          <div className="config-panel-header">Configure Edge</div>
          <div className="edge-type-indicator">
            <span className={`edge-type-dot ${edgeTypeClassName(edgeType)}`} />
            <span>{edgeTypeLabel(edgeType)}</span>
          </div>
          <div className="config-field">
            <label htmlFor="edge-max-traversals">Max Traversals</label>
            <input
              id="edge-max-traversals"
              type="number"
              min={0}
              value={edgeMaxTraversals}
              onChange={(event) => onEdgeMaxTraversalsChange(event.target.value)}
              placeholder="Unlimited"
            />
            {edgeError && <div className="validation-error">{edgeError}</div>}
          </div>
          <div className="edge-config-actions">
            <button className="connect-edge-btn" onClick={onConfirmEdge}>
              Connect
            </button>
            <button className="cancel-edge-btn" onClick={onCancelEdge}>
              Cancel
            </button>
          </div>
        </div>
      )}

      {showEdgeDeletePanel && selectedEdge && (
        <div className="edge-delete-panel react-flow__panel react-flow__panel-bottom-center">
          <div className="config-panel-header">Delete Edge</div>
          <div className="edge-info-row">
            <span className="edge-info-label">Type</span>
            <span className="edge-info-value">
              <span className={`edge-type-dot ${edgeTypeClassName(selectedEdgeType)}`} />
              {edgeTypeLabel(selectedEdgeType)}
            </span>
          </div>
          <div className="edge-info-row">
            <span className="edge-info-label">From</span>
            <span className="edge-info-value">{nodeLabel(nodes, selectedEdge.source)}</span>
          </div>
          <div className="edge-info-row">
            <span className="edge-info-label">To</span>
            <span className="edge-info-value">{nodeLabel(nodes, selectedEdge.target)}</span>
          </div>
          <div className="edge-config-actions">
            <button className="delete-edge-btn" onClick={onDeleteEdge}>
              Delete
            </button>
            <button className="cancel-edge-btn" onClick={onCancelEdge}>
              Cancel
            </button>
          </div>
        </div>
      )}
    </>
  );
}
