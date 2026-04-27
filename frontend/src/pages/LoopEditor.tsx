import { useState, useEffect } from "react";
import { LoopTemplate, LoopStep, LoopStepType } from "../types";
import { loopTemplateService } from "../services/auth";

export default function LoopEditor() {
  const [templates, setTemplates] = useState<LoopTemplate[]>([]);
  const [editingTemplate, setEditingTemplate] = useState<LoopTemplate | null>(null);
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [intervalMinutes, setIntervalMinutes] = useState(60);
  const [steps, setSteps] = useState<LoopStep[]>([]);

  useEffect(() => {
    loadTemplates();
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
    setIntervalMinutes(template.intervalMinutes);
    setSteps(template.steps);
  };

  const openCreate = () => {
    setEditingTemplate(null);
    setName("");
    setDescription("");
    setIntervalMinutes(60);
    setSteps([]);
  };

  const handleSave = async () => {
    const data: Partial<LoopTemplate> = {
      name,
      description,
      intervalMinutes,
      steps,
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

  const addStep = () => {
    setSteps([
      ...steps,
      {
        id: crypto.randomUUID(),
        order: steps.length,
        type: LoopStepType.ApiCall,
        config: {},
        condition: null,
      },
    ]);
  };

  const removeStep = (index: number) => {
    setSteps(steps.filter((_, i) => i !== index));
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
                Every {template.intervalMinutes}m &middot; {template.steps.length} steps
              </div>
              <div className={`loop-status-badge ${template.isActive ? "active" : "inactive"}`}>
                {template.isActive ? "Active" : "Inactive"}
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
            <label>Interval (minutes)</label>
            <input
              type="number"
              value={intervalMinutes}
              onChange={(e) => setIntervalMinutes(Number(e.target.value))}
              min={1}
            />
          </div>
          <div className="form-group">
            <label>Steps</label>
            <div className="loop-steps-list">
              {steps.map((step, index) => (
                <div key={step.id} className="loop-step-item">
                  <span className="loop-step-order">{index + 1}</span>
                  <span className="loop-step-type">{step.type}</span>
                  <button className="btn btn-small" onClick={() => removeStep(index)}>
                    Remove
                  </button>
                </div>
              ))}
            </div>
            <button className="btn btn-secondary" onClick={addStep}>
              + Add Step
            </button>
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

        .loop-status-badge {
          display: inline-block;
          font-size: 0.675rem;
          padding: 0.1rem 0.4rem;
          border-radius: 0.25rem;
          margin-top: 0.25rem;
        }

        .loop-status-badge.active {
          background-color: #065f46;
          color: #6ee7b7;
        }

        .loop-status-badge.inactive {
          background-color: #3a3a5c;
          color: #a0a0b0;
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

        .loop-steps-list {
          display: flex;
          flex-direction: column;
          gap: 0.5rem;
          margin-bottom: 0.5rem;
        }

        .loop-step-item {
          display: flex;
          align-items: center;
          gap: 0.5rem;
          padding: 0.5rem;
          background-color: #2a2a40;
          border-radius: 0.375rem;
        }

        .loop-step-order {
          font-size: 0.75rem;
          color: #707090;
          width: 1.5rem;
        }

        .loop-step-type {
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
