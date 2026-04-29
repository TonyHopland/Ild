import { useState, useEffect, useCallback } from "react";
import {
  ReactFlow,
  Background,
  Controls,
  useNodesState,
  useEdgesState,
  Panel,
  type Node,
  type Edge,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { LoopTemplate } from "../types";
import { loopTemplateService } from "../services/auth";
import LoopNodeComponent from "../components/LoopNodeComponent";
import { templateToNodes, templateToEdges } from "../utils/loopGraphConverter";

const nodeTypes = {
  loopNode: LoopNodeComponent,
};

export default function LoopEditor() {
  const [templates, setTemplates] = useState<LoopTemplate[]>([]);
  const [selectedTemplate, setSelectedTemplate] = useState<LoopTemplate | null>(null);
  const [nodes, setNodes, onNodesChange] = useNodesState<Node>([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([]);

  useEffect(() => {
    void loadTemplates();
  }, []);

  const loadTemplates = async () => {
    try {
      const data = await loopTemplateService.getAll();
      setTemplates(data);
    } catch (error) {
      console.error("Failed to load loop templates:", error);
    }
  };

  const selectTemplate = useCallback((template: LoopTemplate) => {
    setSelectedTemplate(template);
    setNodes(templateToNodes(template));
    setEdges(templateToEdges(template));
  }, []);

  return (
    <div className="page-container">
      <div className="loop-editor-header">
        <h1 className="page-title">Loop Editor</h1>
      </div>

      <div className="loop-editor-layout">
        <div className="loop-list">
          {templates.map((template) => (
            <div
              key={template.id}
              className={`loop-list-item ${selectedTemplate?.id === template.id ? "active" : ""}`}
              onClick={() => selectTemplate(template)}
            >
              <div className="loop-list-item-name">{template.name}</div>
              <div className="loop-list-item-meta">
                v{template.version} &middot; {template.nodes.length} nodes
              </div>
            </div>
          ))}
        </div>

        <div className="loop-canvas-container">
          {selectedTemplate ? (
            <ReactFlow
              nodes={nodes}
              edges={edges}
              onNodesChange={onNodesChange}
              onEdgesChange={onEdgesChange}
              nodeTypes={nodeTypes}
              fitView
              panOnDrag={true}
              zoomOnScroll={true}
              elementsSelectable={true}
            >
              <Background />
              <Controls />
              <Panel position="top-right" className="loop-canvas-info">
                <span>{selectedTemplate.name}</span>
                <span>v{selectedTemplate.version}</span>
              </Panel>
            </ReactFlow>
          ) : (
            <div className="loop-canvas-empty">Select a template to view its graph</div>
          )}
        </div>
      </div>
      <style>{`
        .loop-editor-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          margin-bottom: 1rem;
        }

        .loop-editor-layout {
          display: grid;
          grid-template-columns: 300px 1fr;
          gap: 1rem;
          height: calc(100vh - 200px);
        }

        .loop-list {
          display: flex;
          flex-direction: column;
          gap: 0.5rem;
          overflow-y: auto;
          max-height: calc(100vh - 200px);
        }

        .loop-list-item {
          padding: 0.75rem;
          background-color: #1e1e30;
          border-radius: 0.375rem;
          cursor: pointer;
          border: 1px solid #2d2d44;
          transition: border-color 0.15s ease;
        }

        .loop-list-item:hover,
        .loop-list-item.active {
          border-color: #6366f1;
        }

        .loop-list-item-name {
          font-size: 0.875rem;
          font-weight: 500;
          color: #e0e0e0;
          margin-bottom: 0.25rem;
        }

        .loop-list-item-meta {
          font-size: 0.75rem;
          color: #707090;
        }

        .loop-canvas-container {
          background-color: #1a1a2e;
          border-radius: 0.5rem;
          border: 1px solid #2d2d44;
          overflow: hidden;
        }

        .loop-canvas-empty {
          display: flex;
          align-items: center;
          justify-content: center;
          height: 100%;
          color: #707090;
          font-size: 0.875rem;
        }

        .loop-canvas-info {
          background: #1e1e30;
          border: 1px solid #2d2d44;
          border-radius: 0.375rem;
          padding: 0.5rem 0.75rem;
          display: flex;
          flex-direction: column;
          gap: 0.25rem;
          font-size: 0.75rem;
          color: #a0a0b0;
        }

        .loop-canvas-info span:first-child {
          font-weight: 600;
          color: #e0e0e0;
        }
      `}</style>
    </div>
  );
}
