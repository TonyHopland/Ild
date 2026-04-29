import { useState, useEffect, useCallback, useRef } from "react";
import {
  ReactFlow,
  Background,
  Controls,
  useNodesState,
  useEdgesState,
  Panel,
  type Node,
  type Edge,
  type Connection,
  addEdge,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { LoopTemplate, NodeType, EdgeType, ConfigFieldDescriptor, AiProvider } from "../types";
import { loopTemplateService, agentAdapterService, aiProviderService } from "../services/auth";
import LoopNodeComponent from "../components/LoopNodeComponent";
import AdapterConfigFields from "../components/AdapterConfigFields";
import {
  templateToNodes,
  templateToEdges,
  nodesToLoopNodes,
  edgesToLoopNodeEdges,
} from "../utils/loopGraphConverter";
import { checkEdgeConstraints, buildEdge } from "../utils/edgeUtils";

const nodeTypes = {
  loopNode: LoopNodeComponent,
};

const paletteItems = [
  { type: NodeType.Start, label: "Start" },
  { type: NodeType.Cmd, label: "Cmd" },
  { type: NodeType.AI, label: "AI" },
  { type: NodeType.Human, label: "Human" },
  { type: NodeType.PR, label: "PR" },
  { type: NodeType.Cleanup, label: "Cleanup" },
];

export default function LoopEditor() {
  const [templates, setTemplates] = useState<LoopTemplate[]>([]);
  const [selectedTemplate, setSelectedTemplate] = useState<LoopTemplate | null>(null);
  const [nodes, setNodes, onNodesChange] = useNodesState<Node>([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([]);
  const reactFlowWrapper = useRef<HTMLDivElement>(null);
  const reactFlowInstance = useRef<any>(null);
  const [selectedNode, setSelectedNode] = useState<Node | null>(null);
  const [nodeLabel, setNodeLabel] = useState("");
  const [cmdCommand, setCmdCommand] = useState("");
  const [cmdTimeout, setCmdTimeout] = useState(30);
  const [aiPrompt, setAiPrompt] = useState("");
  const [aiProvider, setAiProvider] = useState("");
  const [aiTools, setAiTools] = useState<string[]>([]);
  const [startCreateWorktree, setStartCreateWorktree] = useState(true);
  const [humanInputLabel, setHumanInputLabel] = useState("");
  const [labelError, setLabelError] = useState<string | null>(null);
  const [pendingConnection, setPendingConnection] = useState<Connection | null>(null);
  const [edgeType, setEdgeType] = useState<EdgeType>(EdgeType.OnSuccess);
  const [edgeMaxTraversals, setEdgeMaxTraversals] = useState("");
  const [edgeError, setEdgeError] = useState<string | null>(null);
  const [selectedEdge, setSelectedEdge] = useState<Edge | null>(null);
  const [saveSuccess, setSaveSuccess] = useState(false);
  const [validationErrors, setValidationErrors] = useState<string[]>([]);
  const [isNewTemplate, setIsNewTemplate] = useState(false);
  const [newTemplateName, setNewTemplateName] = useState("");
  const [cloningTemplateId, setCloningTemplateId] = useState<string | null>(null);
  const [cloneName, setCloneName] = useState("");
  const [showVersionHistory, setShowVersionHistory] = useState(false);
  const [versionHistory, setVersionHistory] = useState<any[]>([]);
  const [readOnlyVersion, setReadOnlyVersion] = useState<number | null>(null);
  const [adapterConfigSchema, setAdapterConfigSchema] = useState<ConfigFieldDescriptor[]>([]);
  const [adapterConfigValues, setAdapterConfigValues] = useState<
    Record<string, string | number | boolean>
  >({});
  const [aiProviders, setAiProviders] = useState<AiProvider[]>([]);

  useEffect(() => {
    void loadTemplates();
    void loadAiProviders();
  }, []);

  const loadTemplates = async () => {
    try {
      const data = await loopTemplateService.getAll();
      setTemplates(data);
    } catch (error) {
      console.error("Failed to load loop templates:", error);
    }
  };

  const loadAiProviders = async () => {
    try {
      const data = await aiProviderService.getAll();
      setAiProviders(data);
    } catch (error) {
      console.error("Failed to load AI providers:", error);
    }
  };

  const selectTemplate = useCallback((template: LoopTemplate) => {
    setSelectedTemplate(template);
    setNodes(templateToNodes(template));
    setEdges(templateToEdges(template));
    setValidationErrors([]);
    setSaveSuccess(false);
    setIsNewTemplate(false);
    setNewTemplateName("");
  }, []);

  const handleNewTemplate = () => {
    setSelectedTemplate(null);
    setNodes([]);
    setEdges([]);
    setValidationErrors([]);
    setSaveSuccess(false);
    setIsNewTemplate(true);
    setNewTemplateName("");
  };

  const handleSave = async () => {
    // Validate nodes exist
    if (nodes.length === 0) {
      setValidationErrors(["Graph must contain at least one node."]);
      return;
    }

    // Client-side validation
    const errors: string[] = [];
    const nodeTypes = nodes.map((n) => (n.data as { type?: string }).type);

    if (!nodeTypes.includes(NodeType.Start)) {
      errors.push("Graph must contain a Start node.");
    }
    if (!nodeTypes.includes(NodeType.Cleanup)) {
      errors.push("Graph must contain a Cleanup node.");
    }

    // Reachability: BFS from Start
    const startNode = nodes.find((n) => (n.data as { type?: string }).type === NodeType.Start);
    if (startNode) {
      const reachable = new Set<string>();
      const queue: string[] = [startNode.id];
      reachable.add(startNode.id);
      while (queue.length > 0) {
        const cur = queue.shift()!;
        for (const e of edges) {
          if (e.source === cur && reachable.add(e.target)) {
            queue.push(e.target);
          }
        }
      }

      // All nodes must be reachable
      const unreachable = nodes.filter((n) => !reachable.has(n.id)).map((n) => n.id);
      if (unreachable.length > 0) {
        errors.push(`Unreachable nodes from Start: ${unreachable.join(", ")}`);
      }

      // At least one Cleanup must be reachable
      const cleanupNodes = nodes.filter(
        (n) => (n.data as { type?: string }).type === NodeType.Cleanup,
      );
      const reachableCleanup = cleanupNodes.some((n) => reachable.has(n.id));
      if (cleanupNodes.length > 0 && !reachableCleanup) {
        errors.push("No path from Start leads to a Cleanup node.");
      }
    }

    if (errors.length > 0) {
      setValidationErrors(errors);
      return;
    }

    const loopNodes = nodesToLoopNodes(nodes);
    const loopEdges = edgesToLoopNodeEdges(edges);

    // Server-side validation for placeholders
    const isValid = await loopTemplateService.validate({
      nodes: loopNodes,
      edges: loopEdges,
    });
    if (!isValid.valid) {
      setValidationErrors(["Graph contains unknown placeholders in node prompts."]);
      return;
    }

    try {
      if (isNewTemplate) {
        await loopTemplateService.create({
          name: newTemplateName,
          description: "",
          nodes: loopNodes,
          edges: loopEdges,
        });
      } else if (selectedTemplate) {
        await loopTemplateService.update(selectedTemplate.id, {
          name: selectedTemplate.name,
          description: selectedTemplate.description,
          nodes: loopNodes,
          edges: loopEdges,
        });
      }
      setSaveSuccess(true);
      setTimeout(() => {
        setSaveSuccess(false);
        setIsNewTemplate(false);
      }, 3000);
      void loadTemplates();
    } catch (error) {
      if (error && typeof error === "object" && "message" in error) {
        setValidationErrors([String((error as { message: string }).message)]);
      }
    }
  };

  const handleClone = async (template: LoopTemplate) => {
    if (!cloneName.trim()) return;
    try {
      await loopTemplateService.clone(template.id, cloneName.trim());
      setCloningTemplateId(null);
      setCloneName("");
      void loadTemplates();
    } catch (error) {
      console.error("Failed to clone template:", error);
    }
  };

  const handleShowVersionHistory = async (template: LoopTemplate) => {
    try {
      const versions = await loopTemplateService.getVersions(template.id);
      setVersionHistory(versions);
      setShowVersionHistory(true);
    } catch (error) {
      console.error("Failed to load versions:", error);
    }
  };

  const handleSelectVersion = async (template: LoopTemplate, versionNumber: number) => {
    try {
      const version = await loopTemplateService.getById(`${template.id}/versions/${versionNumber}`);
      setSelectedTemplate(version);
      setNodes(templateToNodes(version));
      setEdges(templateToEdges(version));
      setReadOnlyVersion(versionNumber);
      setShowVersionHistory(false);
      setValidationErrors([]);
      setSaveSuccess(false);
      setIsNewTemplate(false);
    } catch (error) {
      console.error("Failed to load version:", error);
    }
  };

  const exitReadOnlyMode = () => {
    setReadOnlyVersion(null);
    setSelectedTemplate(null);
    setNodes([]);
    setEdges([]);
  };

  const onInit = useCallback((flow: any) => {
    reactFlowInstance.current = flow;
  }, []);

  const onDrop = useCallback(
    (event: React.DragEvent) => {
      event.preventDefault();
      event.stopPropagation();

      const nodeType = event.dataTransfer.getData("application/loop-node-type");
      if (!nodeType || !reactFlowWrapper.current || !reactFlowInstance.current) {
        return;
      }

      const bounds = reactFlowWrapper.current.getBoundingClientRect();
      const position = reactFlowInstance.current.screenToFlowPosition({
        x: event.clientX - bounds.left,
        y: event.clientY - bounds.top,
      });

      const id = `node-${Date.now()}`;

      const newNode: Node = {
        id,
        type: "loopNode",
        position,
        data: {
          label: nodeType,
          type: nodeType,
        },
      };

      setNodes((nds) => nds.concat(newNode));
    },
    [setNodes],
  );

  const onDragOver = useCallback((event: React.DragEvent) => {
    event.preventDefault();
    event.dataTransfer.dropEffect = "move";
  }, []);

  const onNodeClick = useCallback((_event: React.MouseEvent, node: Node) => {
    setSelectedNode(node);
    const data = node.data as {
      label: string;
      type: string;
      config?: Record<string, unknown>;
    };
    setNodeLabel(data.label || "");
    const config = data.config || {};
    setCmdCommand((config.command as string) || "");
    setCmdTimeout((config.timeout as number) ?? 30);
    setAiPrompt((config.promptTemplate as string) || "");
    setAiProvider((config.aiProviderId as string) || "");
    setAiTools((config.toolAllowlist as string[]) || []);
    setStartCreateWorktree((config.createWorktree as boolean) ?? true);
    setHumanInputLabel((config.inputLabel as string) || "");

    if (data.type === NodeType.AI) {
      const providerType = "openai";
      void agentAdapterService.getConfigSchema(providerType).then((schema) => {
        setAdapterConfigSchema(schema);
        const initialValues: Record<string, string | number | boolean> = {};
        for (const field of schema) {
          if (field.defaultValue !== null && field.defaultValue !== undefined) {
            initialValues[field.name] = field.defaultValue;
          }
        }
        setAdapterConfigValues(initialValues);
      });
    }
  }, []);

  const updateNodeLabel = useCallback(
    (newLabel: string) => {
      if (!selectedNode) return;
      setNodeLabel(newLabel);
      setLabelError(null);
      setNodes((nds) =>
        nds.map((nd) =>
          nd.id === selectedNode.id ? { ...nd, data: { ...nd.data, label: newLabel } } : nd,
        ),
      );
    },
    [selectedNode, setNodes],
  );

  const validateLabel = useCallback(
    (label: string) => {
      if (!label.trim()) {
        setLabelError("Label is required");
        return false;
      }
      const duplicates = nodes.filter(
        (nd) =>
          nd.id !== selectedNode?.id &&
          (nd.data as { label?: string }).label?.trim() === label.trim(),
      );
      if (duplicates.length > 0) {
        setLabelError("Label must be unique");
        return false;
      }
      setLabelError(null);
      return true;
    },
    [selectedNode, nodes],
  );

  const deleteSelectedNode = useCallback(() => {
    if (!selectedNode) return;
    setNodes((nds) => nds.filter((nd) => nd.id !== selectedNode.id));
    setEdges((eds) =>
      eds.filter((ed) => ed.source !== selectedNode.id && ed.target !== selectedNode.id),
    );
    setSelectedNode(null);
  }, [selectedNode, setNodes, setEdges]);

  const updateNodeConfig = useCallback(
    (configUpdate: Record<string, unknown>) => {
      if (!selectedNode) return;
      const currentConfig =
        (selectedNode.data as { config?: Record<string, unknown> }).config || {};
      setNodes((nds) =>
        nds.map((nd) =>
          nd.id === selectedNode.id
            ? {
                ...nd,
                data: { ...nd.data, config: { ...currentConfig, ...configUpdate } },
              }
            : nd,
        ),
      );
    },
    [selectedNode, setNodes],
  );

  const handleAdapterConfigChange = useCallback(
    (name: string, value: string | number | boolean) => {
      if (!selectedNode) return;
      setAdapterConfigValues((prev) => ({ ...prev, [name]: value }));
      const currentConfig =
        (selectedNode.data as { config?: Record<string, unknown> }).config || {};
      const adapterConfig = (currentConfig.adapterConfig as Record<string, unknown>) || {};
      setNodes((nds) =>
        nds.map((nd) =>
          nd.id === selectedNode?.id
            ? {
                ...nd,
                data: {
                  ...nd.data,
                  config: { ...currentConfig, adapterConfig: { ...adapterConfig, [name]: value } },
                },
              }
            : nd,
        ),
      );
    },
    [selectedNode, setNodes],
  );

  const onConnect = useCallback(
    (connection: Connection) => {
      const sourceNode = nodes.find((n) => n.id === connection.source);
      if (!sourceNode) return;

      const result = checkEdgeConstraints(connection.source, edges);

      if (!result.allowed) {
        setEdgeError(result.error ?? "Cannot create edge");
        return;
      }

      setEdgeType(result.suggestedType ?? EdgeType.OnSuccess);
      setEdgeMaxTraversals("");
      setEdgeError(null);
      setPendingConnection(connection);
    },
    [nodes, edges],
  );

  const confirmEdge = useCallback(() => {
    if (!pendingConnection) return;

    if (
      edgeMaxTraversals !== "" &&
      (isNaN(Number(edgeMaxTraversals)) || Number(edgeMaxTraversals) < 0)
    ) {
      setEdgeError("Max traversals must be a non-negative number");
      return;
    }

    const newEdge = buildEdge({
      source: pendingConnection.source,
      target: pendingConnection.target,
      edgeType,
      maxTraversals: edgeMaxTraversals !== "" ? Number(edgeMaxTraversals) : null,
    });

    setEdges((eds) => addEdge(newEdge, eds));
    setPendingConnection(null);
    setEdgeMaxTraversals("");
    setEdgeError(null);
  }, [pendingConnection, edgeType, edgeMaxTraversals, setEdges]);

  const cancelEdge = useCallback(() => {
    setPendingConnection(null);
    setEdgeMaxTraversals("");
    setEdgeError(null);
  }, []);

  const onEdgeClick = useCallback((_event: React.MouseEvent, edge: Edge) => {
    setSelectedEdge(edge);
    setSelectedNode(null);
  }, []);

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if ((e.key === "Delete" || e.code === "Delete") && selectedEdge) {
        setEdges((eds) => eds.filter((ed) => ed.id !== selectedEdge.id));
        setSelectedEdge(null);
      }
    };

    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [selectedEdge, setEdges]);

  return (
    <div className="page-container">
      <div className="loop-editor-header">
        <h1 className="page-title">Loop Editor</h1>
        {readOnlyVersion !== null && (
          <div className="readonly-banner" onClick={exitReadOnlyMode}>
            Viewing v{readOnlyVersion} (read-only) — click to exit
          </div>
        )}
        <div className="header-actions">
          {isNewTemplate && (
            <input
              type="text"
              className="new-template-name-input"
              placeholder="Template name"
              value={newTemplateName}
              onChange={(e) => setNewTemplateName(e.target.value)}
            />
          )}
          {saveSuccess && <span className="save-success">Saved!</span>}
          {(selectedTemplate || isNewTemplate) && readOnlyVersion === null && (
            <button className="save-btn" onClick={handleSave}>
              Save
            </button>
          )}
          <button className="new-template-btn" onClick={handleNewTemplate}>
            New Template
          </button>
        </div>
      </div>

      <div className="loop-editor-layout">
        <div className={`node-palette ${readOnlyVersion !== null ? "palette-disabled" : ""}`}>
          <div className="palette-header">Drag & Drop</div>
          {paletteItems.map((item) => (
            <div
              key={item.type}
              className="palette-item"
              draggable={readOnlyVersion === null}
              onDragStart={(e) => {
                e.dataTransfer.setData("application/loop-node-type", item.type);
              }}
            >
              {item.label}
            </div>
          ))}
        </div>

        <div className="loop-list">
          {!showVersionHistory ? (
            <>
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
                  {cloningTemplateId === template.id ? (
                    <div className="clone-input-row">
                      <input
                        type="text"
                        className="clone-name-input"
                        placeholder="Clone name"
                        value={cloneName}
                        onClick={(e) => e.stopPropagation()}
                        onChange={(e) => setCloneName(e.target.value)}
                        onKeyDown={(e) => {
                          if (e.key === "Enter") void handleClone(template);
                        }}
                      />
                      <button
                        className="clone-confirm-btn"
                        onClick={(e) => {
                          e.stopPropagation();
                          void handleClone(template);
                        }}
                      >
                        Clone
                      </button>
                    </div>
                  ) : (
                    <div className="loop-list-item-actions">
                      <button
                        className="clone-btn"
                        onClick={(e) => {
                          e.stopPropagation();
                          setCloningTemplateId(template.id);
                          setCloneName(`Copy of ${template.name}`);
                        }}
                      >
                        Clone
                      </button>
                      <button
                        className="versions-btn"
                        onClick={(e) => {
                          e.stopPropagation();
                          void handleShowVersionHistory(template);
                        }}
                      >
                        Versions
                      </button>
                    </div>
                  )}
                </div>
              ))}
            </>
          ) : (
            <div className="version-history-list">
              <div className="version-history-header">
                <span>Version History</span>
                <button
                  className="back-to-templates-btn"
                  onClick={() => setShowVersionHistory(false)}
                >
                  ← Back
                </button>
              </div>
              {versionHistory.map((v) => (
                <div
                  key={v.id}
                  className="version-history-item"
                  onClick={() => {
                    const template = templates.find((t) => t.id === v.loopTemplateId);
                    if (template) void handleSelectVersion(template, v.versionNumber);
                  }}
                >
                  <div className="version-number">v{v.versionNumber}</div>
                  <div className="version-meta">
                    {new Date(v.createdAt).toLocaleDateString()} &middot; {v.nodeCount ?? "—"} nodes
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>

        <div
          className="loop-canvas-container"
          ref={reactFlowWrapper}
          onDrop={onDrop}
          onDragOver={onDragOver}
        >
          {selectedTemplate || isNewTemplate ? (
            <ReactFlow
              nodes={nodes}
              edges={edges}
              onNodesChange={onNodesChange}
              onEdgesChange={onEdgesChange}
              nodeTypes={nodeTypes}
              onInit={onInit}
              onNodeClick={onNodeClick}
              onConnect={onConnect}
              onEdgeClick={onEdgeClick}
              fitView
              panOnDrag={true}
              zoomOnScroll={true}
              elementsSelectable={true}
            >
              <Background />
              <Controls />
              <Panel position="top-right" className="loop-canvas-info">
                <span>{selectedTemplate?.name ?? newTemplateName ?? "Untitled"}</span>
                <span>v{selectedTemplate?.version ?? "new"}</span>
              </Panel>

              {validationErrors.length > 0 && (
                <Panel position="top-center" className="validation-errors-panel">
                  <div className="validation-errors-header">Validation Errors</div>
                  {validationErrors.map((err, i) => (
                    <div key={i} className="validation-error-badge">
                      {err}
                    </div>
                  ))}
                </Panel>
              )}

              {selectedNode && (
                <Panel position="top-left" className="node-config-panel">
                  <div className="config-panel-header">Node Config</div>
                  <div className="config-field">
                    <label htmlFor="node-label">Label</label>
                    <input
                      id="node-label"
                      type="text"
                      value={nodeLabel}
                      onChange={(e) => updateNodeLabel(e.target.value)}
                      onBlur={(e) => validateLabel(e.target.value)}
                      className={labelError ? "input-error" : ""}
                    />
                    {labelError && <div className="validation-error">{labelError}</div>}
                  </div>
                  <div className="config-field">
                    <label>Type</label>
                    <div className="config-read-only">
                      {(selectedNode.data as { type: string }).type}
                    </div>
                  </div>

                  {(selectedNode.data as { type: string }).type === NodeType.Cmd && (
                    <>
                      <div className="config-field">
                        <label htmlFor="cmd-command">Command</label>
                        <input
                          id="cmd-command"
                          type="text"
                          value={cmdCommand}
                          onChange={(e) => {
                            setCmdCommand(e.target.value);
                            updateNodeConfig({ command: e.target.value });
                          }}
                        />
                      </div>
                      <div className="config-field">
                        <label htmlFor="cmd-timeout">Timeout</label>
                        <input
                          id="cmd-timeout"
                          type="number"
                          min={1}
                          value={cmdTimeout}
                          onChange={(e) => {
                            const val = parseInt(e.target.value, 10) || 30;
                            setCmdTimeout(val);
                            updateNodeConfig({ timeout: val });
                          }}
                        />
                      </div>
                    </>
                  )}

                  {(selectedNode.data as { type: string }).type === NodeType.AI && (
                    <>
                      <div className="config-field">
                        <label htmlFor="ai-prompt">Prompt Template</label>
                        <textarea
                          id="ai-prompt"
                          rows={3}
                          value={aiPrompt}
                          onChange={(e) => {
                            setAiPrompt(e.target.value);
                            updateNodeConfig({ promptTemplate: e.target.value });
                          }}
                        />
                      </div>
                      <div className="config-field">
                        <label htmlFor="ai-provider">AI Provider</label>
                        <select
                          id="ai-provider"
                          value={aiProvider}
                          onChange={(e) => {
                            setAiProvider(e.target.value);
                            updateNodeConfig({ aiProviderId: e.target.value });
                            const selected = aiProviders.find((p) => p.id === e.target.value);
                            if (selected) {
                              void agentAdapterService
                                .getConfigSchema(selected.type)
                                .then((schema) => {
                                  setAdapterConfigSchema(schema);
                                  const initialValues: Record<string, string | number | boolean> =
                                    {};
                                  for (const field of schema) {
                                    if (
                                      field.defaultValue !== null &&
                                      field.defaultValue !== undefined
                                    ) {
                                      initialValues[field.name] = field.defaultValue;
                                    }
                                  }
                                  setAdapterConfigValues(initialValues);
                                });
                            } else {
                              setAdapterConfigSchema([]);
                              setAdapterConfigValues({});
                            }
                          }}
                        >
                          <option value="">Default</option>
                          {aiProviders.map((provider) => (
                            <option key={provider.id} value={provider.id}>
                              {provider.name}
                            </option>
                          ))}
                        </select>
                      </div>
                      {adapterConfigSchema.length > 0 && (
                        <AdapterConfigFields
                          schema={adapterConfigSchema}
                          values={adapterConfigValues}
                          onChange={handleAdapterConfigChange}
                        />
                      )}
                      <div className="config-field">
                        <label>Tool Allowlist</label>
                        <div className="tool-checklist">
                          {["read", "write", "execute"].map((tool) => (
                            <label key={tool} className="checkbox-label">
                              <input
                                type="checkbox"
                                checked={aiTools.includes(tool)}
                                onChange={(e) => {
                                  const updated = e.target.checked
                                    ? [...aiTools, tool]
                                    : aiTools.filter((t) => t !== tool);
                                  setAiTools(updated);
                                  updateNodeConfig({ toolAllowlist: updated });
                                }}
                              />
                              {tool}
                            </label>
                          ))}
                        </div>
                      </div>
                    </>
                  )}

                  {(selectedNode.data as { type: string }).type === NodeType.Start && (
                    <div className="config-field">
                      <label className="checkbox-label">
                        <input
                          type="checkbox"
                          checked={startCreateWorktree}
                          onChange={(e) => {
                            setStartCreateWorktree(e.target.checked);
                            updateNodeConfig({ createWorktree: e.target.checked });
                          }}
                        />
                        Create worktree
                      </label>
                    </div>
                  )}

                  {(selectedNode.data as { type: string }).type === NodeType.Human && (
                    <div className="config-field">
                      <label htmlFor="human-input-label">Input Label</label>
                      <input
                        id="human-input-label"
                        type="text"
                        value={humanInputLabel}
                        onChange={(e) => {
                          setHumanInputLabel(e.target.value);
                          updateNodeConfig({ inputLabel: e.target.value });
                        }}
                      />
                    </div>
                  )}

                  <button className="delete-node-btn" onClick={deleteSelectedNode}>
                    Delete
                  </button>
                </Panel>
              )}

              {pendingConnection && (
                <Panel position="bottom-center" className="edge-config-panel">
                  <div className="config-panel-header">Configure Edge</div>
                  <div className="config-field">
                    <label htmlFor="edge-type">Edge Type</label>
                    <select
                      id="edge-type"
                      value={edgeType}
                      onChange={(e) => setEdgeType(e.target.value as EdgeType)}
                      disabled={
                        edges.some(
                          (ed) =>
                            ed.source === pendingConnection.source &&
                            ed.data?.edgeType === EdgeType.OnSuccess,
                        ) &&
                        edges.some(
                          (ed) =>
                            ed.source === pendingConnection.source &&
                            ed.data?.edgeType === EdgeType.OnFailure,
                        )
                      }
                    >
                      <option value={EdgeType.OnSuccess}>OnSuccess</option>
                      <option value={EdgeType.OnFailure}>OnFailure</option>
                    </select>
                  </div>
                  <div className="config-field">
                    <label htmlFor="edge-max-traversals">Max Traversals</label>
                    <input
                      id="edge-max-traversals"
                      type="number"
                      min={0}
                      value={edgeMaxTraversals}
                      onChange={(e) => setEdgeMaxTraversals(e.target.value)}
                      placeholder="Unlimited"
                    />
                    {edgeError && <div className="validation-error">{edgeError}</div>}
                  </div>
                  <div className="edge-config-actions">
                    <button className="connect-edge-btn" onClick={confirmEdge}>
                      Connect
                    </button>
                    <button className="cancel-edge-btn" onClick={cancelEdge}>
                      Cancel
                    </button>
                  </div>
                </Panel>
              )}
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

        .header-actions {
          display: flex;
          align-items: center;
          gap: 0.75rem;
        }

        .save-btn {
          padding: 0.375rem 1rem;
          background: #064e3b;
          border: 1px solid #10b981;
          border-radius: 0.25rem;
          color: #10b981;
          font-size: 0.8rem;
          font-weight: 600;
          cursor: pointer;
          transition: background 0.15s ease;
        }

        .save-btn:hover {
          background: #047857;
        }

        .save-success {
          color: #10b981;
          font-size: 0.8rem;
          font-weight: 600;
        }

        .new-template-btn {
          padding: 0.375rem 1rem;
          background: #1a1a2e;
          border: 1px solid #6366f1;
          border-radius: 0.25rem;
          color: #6366f1;
          font-size: 0.8rem;
          font-weight: 600;
          cursor: pointer;
          transition: background 0.15s ease;
        }

        .new-template-btn:hover {
          background: #2d2d44;
        }

        .new-template-name-input {
          padding: 0.375rem 0.5rem;
          background: #1a1a2e;
          border: 1px solid #2d2d44;
          border-radius: 0.25rem;
          color: #e0e0e0;
          font-size: 0.8rem;
          outline: none;
          width: 180px;
        }

        .new-template-name-input:focus {
          border-color: #6366f1;
        }

        .validation-errors-panel {
          background: #4c0519;
          border: 1px solid #ef4444;
          border-radius: 0.375rem;
          padding: 0.75rem;
          min-width: 250px;
        }

        .validation-errors-header {
          font-size: 0.8rem;
          font-weight: 600;
          color: #ef4444;
          margin-bottom: 0.5rem;
        }

        .validation-error-badge {
          font-size: 0.75rem;
          color: #fca5a5;
          padding: 0.25rem 0;
        }

        .loop-editor-layout {
          display: grid;
          grid-template-columns: 100px 260px 1fr;
          gap: 1rem;
          height: calc(100vh - 200px);
        }

        .node-palette {
          display: flex;
          flex-direction: column;
          gap: 0.25rem;
          padding: 0.5rem;
          background-color: #1e1e30;
          border-radius: 0.375rem;
          border: 1px solid #2d2d44;
          overflow-y: auto;
        }

        .palette-header {
          font-size: 0.7rem;
          font-weight: 600;
          color: #707090;
          text-transform: uppercase;
          letter-spacing: 0.05em;
          margin-bottom: 0.25rem;
          padding: 0 0.25rem;
        }

        .palette-item {
          padding: 0.5rem 0.25rem;
          background-color: #1a1a2e;
          border-radius: 0.25rem;
          cursor: grab;
          border: 1px solid #2d2d44;
          font-size: 0.75rem;
          color: #a0a0b0;
          text-align: center;
          transition: border-color 0.15s ease;
          user-select: none;
        }

        .palette-item:hover {
          border-color: #6366f1;
          color: #e0e0e0;
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

        .node-config-panel {
          background: #1e1e30;
          border: 1px solid #2d2d44;
          border-radius: 0.375rem;
          padding: 0.75rem;
          min-width: 200px;
        }

        .config-panel-header {
          font-size: 0.8rem;
          font-weight: 600;
          color: #e0e0e0;
          margin-bottom: 0.75rem;
        }

        .config-field {
          margin-bottom: 0.5rem;
        }

        .config-field label {
          display: block;
          font-size: 0.7rem;
          color: #707090;
          margin-bottom: 0.25rem;
        }

        .config-field input[type="text"],
        .config-field input[type="number"],
        .config-field textarea,
        .config-field select {
          width: 100%;
          padding: 0.375rem 0.5rem;
          background: #1a1a2e;
          border: 1px solid #2d2d44;
          border-radius: 0.25rem;
          color: #e0e0e0;
          font-size: 0.8rem;
          outline: none;
        }

        .config-field input:focus,
        .config-field textarea:focus,
        .config-field select:focus {
          border-color: #6366f1;
        }

        .input-error {
          border-color: #ef4444 !important;
        }

        .validation-error {
          font-size: 0.7rem;
          color: #ef4444;
          margin-top: 0.25rem;
        }

        .config-read-only {
          padding: 0.375rem 0.5rem;
          background: #1a1a2e;
          border-radius: 0.25rem;
          color: #a0a0b0;
          font-size: 0.8rem;
        }

        .config-field textarea {
          resize: vertical;
          font-family: inherit;
        }

        .checkbox-label {
          display: flex;
          align-items: center;
          gap: 0.5rem;
          font-size: 0.8rem;
          color: #e0e0e0;
          cursor: pointer;
        }

        .checkbox-label input[type="checkbox"] {
          width: auto;
        }

        .tool-checklist {
          display: flex;
          flex-direction: column;
          gap: 0.25rem;
        }

        .delete-node-btn {
          width: 100%;
          margin-top: 0.75rem;
          padding: 0.375rem 0.5rem;
          background: #4c0519;
          border: 1px solid #ef4444;
          border-radius: 0.25rem;
          color: #ef4444;
          font-size: 0.75rem;
          font-weight: 600;
          cursor: pointer;
          transition: background 0.15s ease;
        }

        .delete-node-btn:hover {
          background: #6b0a1f;
        }

        .edge-config-panel {
          background: #1e1e30;
          border: 1px solid #2d2d44;
          border-radius: 0.375rem;
          padding: 0.75rem;
          min-width: 220px;
        }

        .edge-config-actions {
          display: flex;
          gap: 0.5rem;
          margin-top: 0.75rem;
        }

        .connect-edge-btn {
          flex: 1;
          padding: 0.375rem 0.5rem;
          background: #064e3b;
          border: 1px solid #10b981;
          border-radius: 0.25rem;
          color: #10b981;
          font-size: 0.75rem;
          font-weight: 600;
          cursor: pointer;
          transition: background 0.15s ease;
        }

        .connect-edge-btn:hover {
          background: #047857;
        }

        .cancel-edge-btn {
          flex: 1;
          padding: 0.375rem 0.5rem;
          background: #1a1a2e;
          border: 1px solid #707090;
          border-radius: 0.25rem;
          color: #a0a0b0;
          font-size: 0.75rem;
          font-weight: 600;
          cursor: pointer;
          transition: background 0.15s ease;
        }

        .cancel-edge-btn:hover {
          background: #2d2d44;
        }

        .readonly-banner {
          background: #1e1e30;
          border: 1px solid #f59e0b;
          border-radius: 0.375rem;
          padding: 0.5rem 1rem;
          color: #f59e0b;
          font-size: 0.8rem;
          font-weight: 600;
          cursor: pointer;
          text-align: center;
          margin-bottom: 0.5rem;
        }

        .readonly-banner:hover {
          background: #2d2d44;
        }

        .loop-list-item-actions {
          display: flex;
          gap: 0.375rem;
          margin-top: 0.375rem;
        }

        .clone-btn,
        .versions-btn {
          padding: 0.125rem 0.5rem;
          background: #1a1a2e;
          border: 1px solid #2d2d44;
          border-radius: 0.25rem;
          color: #707090;
          font-size: 0.65rem;
          font-weight: 600;
          cursor: pointer;
          transition: all 0.15s ease;
        }

        .clone-btn:hover,
        .versions-btn:hover {
          border-color: #6366f1;
          color: #e0e0e0;
        }

        .clone-input-row {
          display: flex;
          gap: 0.375rem;
          margin-top: 0.375rem;
        }

        .clone-name-input {
          flex: 1;
          padding: 0.25rem 0.375rem;
          background: #1a1a2e;
          border: 1px solid #2d2d44;
          border-radius: 0.25rem;
          color: #e0e0e0;
          font-size: 0.7rem;
          outline: none;
        }

        .clone-name-input:focus {
          border-color: #6366f1;
        }

        .clone-confirm-btn {
          padding: 0.125rem 0.5rem;
          background: #064e3b;
          border: 1px solid #10b981;
          border-radius: 0.25rem;
          color: #10b981;
          font-size: 0.65rem;
          font-weight: 600;
          cursor: pointer;
        }

        .clone-confirm-btn:hover {
          background: #047857;
        }

        .version-history-list {
          display: flex;
          flex-direction: column;
          gap: 0.375rem;
        }

        .version-history-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          margin-bottom: 0.25rem;
          font-size: 0.75rem;
          font-weight: 600;
          color: #e0e0e0;
        }

        .back-to-templates-btn {
          padding: 0.125rem 0.5rem;
          background: transparent;
          border: none;
          color: #6366f1;
          font-size: 0.7rem;
          cursor: pointer;
        }

        .back-to-templates-btn:hover {
          color: #818cf8;
        }

        .version-history-item {
          padding: 0.5rem 0.75rem;
          background-color: #1e1e30;
          border-radius: 0.375rem;
          cursor: pointer;
          border: 1px solid #2d2d44;
          transition: border-color 0.15s ease;
        }

        .version-history-item:hover {
          border-color: #6366f1;
        }

        .version-number {
          font-size: 0.8rem;
          font-weight: 600;
          color: #e0e0e0;
        }

        .version-meta {
          font-size: 0.65rem;
          color: #707090;
          margin-top: 0.125rem;
        }

        .palette-disabled {
          opacity: 0.4;
          pointer-events: none;
        }
      `}</style>
    </div>
  );
}
