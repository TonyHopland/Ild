import { useState, useEffect } from "react";
import { LoopTemplate, LoopNode, NodeType, LoopNodeEdge } from "../types";
import { loopTemplateService } from "../services/auth";

export default function LoopEditor() {
  const [templates, setTemplates] = useState<LoopTemplate[]>([]);
  const [editingTemplate, setEditingTemplate] = useState<LoopTemplate | null>(null);
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [nodes, setNodes] = useState<LoopNode[]>([]);
  const [edges, setEdges] = useState<LoopNodeEdge[]>([]);

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

  const openEdit = (template: LoopTemplate) => {
    setEditingTemplate(template);
    setName(template.name);
    setDescription(template.description);
    setNodes(template.nodes);
    setEdges(template.edges);
  };

  const openCreate = () => {
    setEditingTemplate(null);
    setName("");
    setDescription("");
    setNodes([]);
    setEdges([]);
  };

  const handleSave = async () => {
    const data: Partial<LoopTemplate> = {
      name,
      description,
      nodes,
      edges,
    };

    try {
      if (editingTemplate) {
        await loopTemplateService.update(editingTemplate.id, data);
      } else {
        await loopTemplateService.create(data);
      }
      await loadTemplates();
      setEditingTemplate(null);
    } catch (error) {
      console.error("Failed to save loop template:", error);
    }
  };

  const addNode = (type: NodeType) => {
    setNodes([
      ...nodes,
      {
        id: crypto.randomUUID(),
        type,
        label: `${type} Node`,
        config: {},
        maxTraversals: null,
        retryCount: null,
        timeoutSeconds: null,
      },
    ]);
  };

  const removeNode = (nodeId: string) => {
    setNodes(nodes.filter((n) => n.id !== nodeId));
    setEdges(edges.filter((e) => e.sourceNodeId !== nodeId && e.targetNodeId !== nodeId));
  };

  return (
    <div className="page-container">
      <div className="loop-editor-header">
        <h1 className="page-title">Loop Editor</h1>
        <button className="btn btn-primary" onClick={openCreate}>
          + New Loop
        </button>
      </div>

      <div className="loop-editor-layout">
        <div className="loop-list">
          {templates.map((template) => (
            <div
              key={template.id}
              className={`loop-list-item ${editingTemplate?.id === template.id ? "active" : ""}`}
              onClick={() => openEdit(template)}
            >
              <div className="loop-list-item-name">{template.name}</div>
              <div className="loop-list-item-meta">
                v{template.version} &middot; {template.nodes.length} nodes
              </div>
            </div>
          ))}
        </div>

        <div className="loop-editor-form">
          <div className="form-group">
            <label>Name</label>
            <input type="text" value={name} onChange={(e) => setName(e.target.value)} />
          </div>
          <div className="form-group">
            <label>Description</label>
            <textarea
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              rows={2}
            />
          </div>
          <div className="form-group">
            <label>Nodes</label>
            <div className="node-buttons">
              {(Object.values(NodeType) as NodeType[]).map((type) => (
                <button
                  key={type}
                  className="btn btn-secondary btn-small"
                  onClick={() => addNode(type)}
                >
                  + {type}
                </button>
              ))}
            </div>
            <div className="nodes-list">
              {nodes.map((node) => (
                <div key={node.id} className="node-item">
                  <span className="node-type-badge">{node.type}</span>
                  <span className="node-label">{node.label}</span>
                  <button className="btn btn-small" onClick={() => removeNode(node.id)}>
                    Remove
                  </button>
                </div>
              ))}
            </div>
          </div>
          <button className="btn btn-primary" onClick={handleSave}>
            {editingTemplate ? "Update Loop" : "Create Loop"}
          </button>
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
        }

        .loop-list {
          display: flex;
          flex-direction: column;
          gap: 0.5rem;
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

        .loop-editor-form {
          background-color: #1e1e30;
          border-radius: 0.5rem;
          padding: 1rem;
          border: 1px solid #2d2d44;
        }

        .loop-editor-form .form-group {
          margin-bottom: 1rem;
        }

        .loop-editor-form label {
          display: block;
          font-size: 0.75rem;
          color: #a0a0b0;
          margin-bottom: 0.25rem;
        }

        .loop-editor-form input,
        .loop-editor-form textarea {
          width: 100%;
          padding: 0.5rem;
          background-color: #2a2a40;
          border: 1px solid #3a3a5c;
          border-radius: 0.375rem;
          color: #e0e0e0;
          font-size: 0.875rem;
        }

        .node-buttons {
          display: flex;
          flex-wrap: wrap;
          gap: 0.25rem;
          margin-bottom: 0.5rem;
        }

        .nodes-list {
          display: flex;
          flex-direction: column;
          gap: 0.25rem;
        }

        .node-item {
          display: flex;
          align-items: center;
          gap: 0.5rem;
          padding: 0.5rem;
          background-color: #2a2a40;
          border-radius: 0.375rem;
        }

        .node-type-badge {
          font-size: 0.7rem;
          padding: 0.125rem 0.375rem;
          background-color: #3a3a5c;
          border-radius: 0.25rem;
          color: #e0e0e0;
          font-weight: 600;
          min-width: 2.5rem;
          text-align: center;
        }

        .node-label {
          font-size: 0.8rem;
          color: #c0c0d0;
          flex: 1;
        }

        .btn-small {
          padding: 0.25rem 0.5rem;
          font-size: 0.7rem;
          background-color: #2d2d44;
          color: #a0a0b0;
          border: none;
          border-radius: 0.25rem;
          cursor: pointer;
        }

        .btn-secondary {
          background-color: #2d2d44;
          color: #a0a0b0;
          padding: 0.5rem 1rem;
          border: none;
          border-radius: 0.375rem;
          cursor: pointer;
          font-size: 0.8rem;
        }

        .btn-primary {
          background-color: #6366f1;
          color: #fff;
          padding: 0.5rem 1rem;
          border: none;
          border-radius: 0.375rem;
          cursor: pointer;
          font-size: 0.875rem;
        }
      `}</style>
    </div>
  );
}
