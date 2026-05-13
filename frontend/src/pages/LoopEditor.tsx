import "./LoopEditor.css";
import { useState, useEffect, useCallback, useRef } from "react";
import { useMediaQuery } from "../hooks/useMediaQuery";
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
  type OnEdgesChange,
  applyEdgeChanges,
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
import ErrorBanner from "../components/ErrorBanner";
import PromptEditor from "../components/PromptEditor";

function loadErrorMessage(error: unknown, fallback: string): string {
  if (error instanceof Error && error.message) return error.message;
  if (typeof error === "string") return error;
  return fallback;
}

const nodeTypes = {
  loopNode: LoopNodeComponent,
};

const paletteItems = [
  { type: NodeType.Start, label: "Start" },
  { type: NodeType.Cmd, label: "Cmd" },
  { type: NodeType.AI, label: "AI" },
  { type: NodeType.Human, label: "Human" },
  { type: NodeType.Prompt, label: "Prompt" },
  { type: NodeType.PR, label: "PR" },
  { type: NodeType.Cleanup, label: "Cleanup" },
];

function collectSessionPlaceholderUsages(nodes: Node[]): Array<{ name: string; count: number }> {
  const counts = new Map<string, number>();

  for (const node of nodes) {
    const data = node.data as { type?: string; config?: Record<string, unknown> };
    if (data.type !== NodeType.AI) continue;

    const config = data.config || {};
    const rawPlaceholder =
      typeof config.sessionPlaceholder === "string" ? config.sessionPlaceholder : "";
    const placeholder = rawPlaceholder.trim();
    if (!placeholder) continue;

    counts.set(placeholder, (counts.get(placeholder) ?? 0) + 1);
  }

  return Array.from(counts.entries())
    .map(([name, count]) => ({ name, count }))
    .sort((left, right) => left.name.localeCompare(right.name));
}

export default function LoopEditor() {
  const [templates, setTemplates] = useState<LoopTemplate[]>([]);
  const [selectedTemplate, setSelectedTemplate] = useState<LoopTemplate | null>(null);
  const [nodes, setNodes, onNodesChange] = useNodesState<Node>([]);
  const [edges, setEdges] = useEdgesState<Edge>([]);

  // Custom onEdgesChange that preserves edge.data (edgeType) and handle refs across all React Flow change events.
  // The default handler from useEdgesState can strip edge.data on certain operations.
  const onEdgesChangeCustom: OnEdgesChange = useCallback(
    (changes) => {
      setEdges((prev) => {
        const next = applyEdgeChanges(changes, prev);
        return next.map((edge) => {
          const prevEdge = prev.find((e) => e.id === edge.id);
          if (!prevEdge) return edge;
          let restored = edge;
          if (prevEdge?.data && !edge.data) {
            restored = { ...restored, data: prevEdge.data };
          }
          if (prevEdge?.sourceHandle && !edge.sourceHandle) {
            restored = { ...restored, sourceHandle: prevEdge.sourceHandle };
          }
          if (prevEdge?.targetHandle && !edge.targetHandle) {
            restored = { ...restored, targetHandle: prevEdge.targetHandle };
          }
          return restored;
        });
      });
    },
    [setEdges],
  );
  const reactFlowWrapper = useRef<HTMLDivElement>(null);
  const reactFlowInstance = useRef<any>(null);
  const [selectedNode, setSelectedNode] = useState<Node | null>(null);
  const [nodeLabel, setNodeLabel] = useState("");
  const [cmdCommand, setCmdCommand] = useState("");
  const [cmdTimeout, setCmdTimeout] = useState(30);
  const [aiPrompt, setAiPrompt] = useState("");
  const [aiProvider, setAiProvider] = useState("");
  const [aiTimeout, setAiTimeout] = useState(300);
  const [aiTools, setAiTools] = useState<string[]>([]);
  const [aiRejectPattern, setAiRejectPattern] = useState("");
  const [aiUseSession, setAiUseSession] = useState(false);
  const [aiSessionPlaceholder, setAiSessionPlaceholder] = useState("");
  const [startCreateWorktree, setStartCreateWorktree] = useState(true);
  const [humanInputLabel, setHumanInputLabel] = useState("");
  const [humanPrompt, setHumanPrompt] = useState("");
  const [promptNodePrompt, setPromptNodePrompt] = useState("");
  const [prDescriptionTemplate, setPrDescriptionTemplate] = useState("");
  const [labelError, setLabelError] = useState<string | null>(null);
  const [pendingConnection, setPendingConnection] = useState<Connection | null>(null);
  const [edgeType, setEdgeType] = useState<EdgeType>(EdgeType.OnSuccess);
  const [edgeMaxTraversals, setEdgeMaxTraversals] = useState("");
  const [edgeError, setEdgeError] = useState<string | null>(null);
  const [selectedEdge, setSelectedEdge] = useState<Edge | null>(null);
  const [showEdgeDeletePanel, setShowEdgeDeletePanel] = useState(false);
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
  const [errorText, setErrorText] = useState("");
  const [isSaving, setIsSaving] = useState(false);
  const [sidebarVisible, setSidebarVisible] = useState(true);
  const [showArchived, setShowArchived] = useState(false);
  const [showNodeSettingsModal, setShowNodeSettingsModal] = useState(false);
  const [originalNodeConfig, setOriginalNodeConfig] = useState<{
    label: string;
    cmdCommand: string;
    cmdTimeout: number;
    aiPrompt: string;
    aiProvider: string;
    aiTimeout: number;
    aiTools: string[];
    aiRejectPattern: string;
    aiUseSession: boolean;
    aiSessionPlaceholder: string;
    startCreateWorktree: boolean;
    humanInputLabel: string;
    humanPrompt: string;
    promptNodePrompt: string;
    adapterConfigValues: Record<string, string | number | boolean>;
  } | null>(null);
  const isNarrow = useMediaQuery("(max-width: 900px)");
  const sessionPlaceholderUsages = collectSessionPlaceholderUsages(nodes);
  const hasKnownSessionPlaceholders = sessionPlaceholderUsages.length > 0;
  const selectedPlaceholderUsage = sessionPlaceholderUsages.find(
    (entry) => entry.name === aiSessionPlaceholder.trim(),
  );
  const renderAiSessionControls = () => {
    if (!aiUseSession) return null;

    return (
      <>
        {hasKnownSessionPlaceholders && (
          <div className="config-field">
            <label htmlFor="ai-session-placeholder-picker">Reuse Existing Placeholder</label>
            <select
              id="ai-session-placeholder-picker"
              value={aiSessionPlaceholder.trim()}
              onChange={(e) => setAiSessionPlaceholder(e.target.value)}
            >
              <option value="">Create or type a new placeholder</option>
              {sessionPlaceholderUsages.map((entry) => (
                <option key={entry.name} value={entry.name}>
                  {entry.name} ({entry.count} node{entry.count === 1 ? "" : "s"})
                </option>
              ))}
            </select>
            <small
              style={{
                color: "#94a3b8",
                marginTop: "0.25rem",
                display: "block",
              }}
            >
              Pick an existing placeholder to keep multiple AI nodes attached to the same
              conversation lane.
            </small>
          </div>
        )}
        <div className="config-field">
          <label htmlFor="ai-session-placeholder">Local Session Placeholder</label>
          <input
            id="ai-session-placeholder"
            type="text"
            value={aiSessionPlaceholder}
            onChange={(e) => setAiSessionPlaceholder(e.target.value)}
            placeholder="e.g. research"
            list="ai-session-placeholder-options"
          />
          <datalist id="ai-session-placeholder-options">
            {sessionPlaceholderUsages.map((entry) => (
              <option key={entry.name} value={entry.name} />
            ))}
          </datalist>
          <small
            style={{
              color: "#94a3b8",
              marginTop: "0.25rem",
              display: "block",
            }}
          >
            This is a design-time name. ILD binds it to the real adapter-generated session id for
            each run.
          </small>
          {selectedPlaceholderUsage && (
            <small
              style={{
                color: "#cbd5e1",
                marginTop: "0.35rem",
                display: "block",
              }}
            >
              Reused by {selectedPlaceholderUsage.count} AI node
              {selectedPlaceholderUsage.count === 1 ? "" : "s"} in this template.
            </small>
          )}
        </div>
        {hasKnownSessionPlaceholders && (
          <div className="config-field">
            <label>Placeholder Library</label>
            <div
              style={{
                display: "flex",
                flexWrap: "wrap",
                gap: "0.5rem",
              }}
            >
              {sessionPlaceholderUsages.map((entry) => {
                const isSelected = entry.name === aiSessionPlaceholder.trim();
                return (
                  <button
                    key={entry.name}
                    type="button"
                    onClick={() => setAiSessionPlaceholder(entry.name)}
                    style={{
                      border: isSelected ? "1px solid #38bdf8" : "1px solid #334155",
                      background: isSelected ? "#082f49" : "#111827",
                      color: "#e2e8f0",
                      borderRadius: "999px",
                      padding: "0.35rem 0.7rem",
                      cursor: "pointer",
                    }}
                  >
                    {entry.name} ({entry.count})
                  </button>
                );
              })}
            </div>
          </div>
        )}
      </>
    );
  };

  useEffect(() => {
    void loadTemplates();
    void loadAiProviders();
  }, []);

  useEffect(() => {
    return () => {
      if (saveTimeoutRef.current) clearTimeout(saveTimeoutRef.current);
    };
  }, []);

  const loadTemplates = async () => {
    try {
      const data = await loopTemplateService.getAll({ includeArchived: true });
      setTemplates(data);
    } catch (error) {
      setErrorText(loadErrorMessage(error, "Failed to load loop templates."));
    }
  };

  const loadAiProviders = async () => {
    try {
      const data = await aiProviderService.getAll();
      setAiProviders(data);
    } catch (error) {
      setErrorText(loadErrorMessage(error, "Failed to load AI providers."));
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

  const saveTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const handleSave = async () => {
    if (isSaving) return;

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

    // Reachability: BFS from Start using adjacency list
    const startNode = nodes.find((n) => (n.data as { type?: string }).type === NodeType.Start);
    if (startNode) {
      const adjacency = new Map<string, string[]>();
      for (const e of edges) {
        const targets = adjacency.get(e.source) ?? [];
        targets.push(e.target);
        adjacency.set(e.source, targets);
      }
      const reachable = new Set<string>();
      const queue: string[] = [startNode.id];
      reachable.add(startNode.id);
      while (queue.length > 0) {
        const cur = queue.shift()!;
        for (const target of adjacency.get(cur) ?? []) {
          if (!reachable.has(target)) {
            reachable.add(target);
            queue.push(target);
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

    setIsSaving(true);
    setValidationErrors([]);

    try {
      const loopNodes = nodesToLoopNodes(nodes);
      const loopEdges = edgesToLoopNodeEdges(edges);

      // Server-side validation for placeholders
      const validationResult = await loopTemplateService.validate({
        nodes: loopNodes,
        edges: loopEdges,
      });
      if (!validationResult.valid) {
        setValidationErrors(validationResult.errors);
        return;
      }

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
      if (saveTimeoutRef.current) clearTimeout(saveTimeoutRef.current);
      saveTimeoutRef.current = setTimeout(() => {
        setSaveSuccess(false);
        setIsNewTemplate(false);
      }, 3000);
      await loadTemplates();
    } catch (error) {
      if (error && typeof error === "object" && "message" in error) {
        setValidationErrors([String((error as { message: string }).message)]);
      } else {
        setValidationErrors(["Failed to save template."]);
      }
    } finally {
      setIsSaving(false);
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
      setErrorText(loadErrorMessage(error, "Failed to clone template."));
    }
  };

  const handleArchiveToggle = async (template: LoopTemplate) => {
    try {
      if (template.isArchived) {
        await loopTemplateService.unarchive(template.id);
      } else {
        await loopTemplateService.archive(template.id);
        if (selectedTemplate?.id === template.id) {
          setSelectedTemplate(null);
          setNodes([]);
          setEdges([]);
        }
      }
      void loadTemplates();
    } catch (error) {
      setErrorText(loadErrorMessage(error, "Failed to update template."));
    }
  };

  const handleShowVersionHistory = async (template: LoopTemplate) => {
    try {
      const versions = await loopTemplateService.getVersions(template.id);
      setVersionHistory(versions);
      setShowVersionHistory(true);
    } catch (error) {
      setErrorText(loadErrorMessage(error, "Failed to load versions."));
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
      setErrorText(loadErrorMessage(error, "Failed to load version."));
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

  const onNodeClick = useCallback(
    (_event: React.MouseEvent, node: Node) => {
      setSelectedNode(node);
      const data = node.data as {
        label: string;
        type: string;
        config?: Record<string, unknown>;
      };
      const config = data.config || {};
      const adapterConfig = (config.adapterConfig as Record<string, unknown>) || {};

      setNodeLabel(data.label || "");
      setCmdCommand((config.command as string) || "");
      setCmdTimeout((config.timeout as number) ?? 30);
      setAiPrompt((config.prompt as string) || "");
      setAiProvider((config.aiProviderId as string) || "");
      setAiTimeout((config.timeout as number) ?? 300);
      setAiTools((config.toolAllowlist as string[]) || []);
      setAiRejectPattern((config.rejectPattern as string) || "");
      setAiUseSession((config.useSession as boolean | undefined) ?? false);
      setAiSessionPlaceholder((config.sessionPlaceholder as string) || "");
      setStartCreateWorktree((config.createWorktree as boolean) ?? true);
      setHumanInputLabel((config.inputLabel as string) || "");
      setHumanPrompt((config.prompt as string) || "");
      setPromptNodePrompt((config.prompt as string) || "");
      setPrDescriptionTemplate((config.prDescriptionTemplate as string) || "");

      if (data.type === NodeType.AI) {
        const selectedProvider = aiProviders.find((p) => p.id === (config.aiProviderId as string));
        if (selectedProvider) {
          void agentAdapterService.getConfigSchema(selectedProvider.type).then((schema) => {
            setAdapterConfigSchema(schema);
            const initialValues: Record<string, string | number | boolean> = {};
            for (const field of schema) {
              const nodeVal = adapterConfig[field.name];
              if (nodeVal !== undefined && typeof nodeVal === "string") {
                initialValues[field.name] = nodeVal;
              } else if (nodeVal !== undefined && typeof nodeVal === "number") {
                initialValues[field.name] = nodeVal;
              } else if (nodeVal !== undefined && typeof nodeVal === "boolean") {
                initialValues[field.name] = nodeVal;
              } else if (field.defaultValue !== null && field.defaultValue !== undefined) {
                initialValues[field.name] = field.defaultValue;
              }
            }
            setAdapterConfigValues(initialValues);
          });
        } else {
          setAdapterConfigSchema([]);
          setAdapterConfigValues({});
        }
      }

      setOriginalNodeConfig({
        label: data.label || "",
        cmdCommand: (config.command as string) || "",
        cmdTimeout: (config.timeout as number) ?? 30,
        aiPrompt: (config.prompt as string) || "",
        aiProvider: (config.aiProviderId as string) || "",
        aiTimeout: (config.timeout as number) ?? 300,
        aiTools: (config.toolAllowlist as string[]) || [],
        aiRejectPattern: (config.rejectPattern as string) || "",
        aiUseSession: (config.useSession as boolean | undefined) ?? false,
        aiSessionPlaceholder: (config.sessionPlaceholder as string) || "",
        startCreateWorktree: (config.createWorktree as boolean) ?? true,
        humanInputLabel: (config.inputLabel as string) || "",
        humanPrompt: (config.prompt as string) || "",
        promptNodePrompt: (config.prompt as string) || "",
        adapterConfigValues: { ...adapterConfigValues },
      });
      setShowNodeSettingsModal(true);
    },
    [adapterConfigValues, aiProviders],
  );

  const handleSaveNodeSettings = useCallback(() => {
    if (!selectedNode) return;
    const nodeType = (selectedNode.data as { type: string }).type;

    if (nodeType === NodeType.AI && aiUseSession && !aiSessionPlaceholder.trim()) {
      setErrorText("AI nodes with Use Session enabled must set a session placeholder.");
      return;
    }

    const config: Record<string, unknown> = {};
    if (nodeType === NodeType.Cmd) {
      config.command = cmdCommand;
      config.timeout = cmdTimeout;
    } else if (nodeType === NodeType.AI) {
      config.prompt = aiPrompt;
      config.useSession = aiUseSession;
      config.aiProviderId = aiProvider;
      config.timeout = aiTimeout;
      config.toolAllowlist = aiTools;
      config.adapterConfig = { ...adapterConfigValues };
      if (aiRejectPattern) config.rejectPattern = aiRejectPattern;
      config.sessionPlaceholder = aiUseSession ? aiSessionPlaceholder.trim() : undefined;
    } else if (nodeType === NodeType.Start) {
      config.createWorktree = startCreateWorktree;
    } else if (nodeType === NodeType.Human) {
      config.inputLabel = humanInputLabel;
      if (humanPrompt) config.prompt = humanPrompt;
    } else if (nodeType === NodeType.Prompt) {
      if (promptNodePrompt) config.prompt = promptNodePrompt;
    } else if (nodeType === NodeType.PR) {
      if (prDescriptionTemplate) config.prDescriptionTemplate = prDescriptionTemplate;
    }
    setNodes((nds) =>
      nds.map((nd) =>
        nd.id === selectedNode.id
          ? {
              ...nd,
              data: {
                ...nd.data,
                label: nodeLabel,
                config: { ...(nd.data as { config?: Record<string, unknown> }).config, ...config },
              },
            }
          : nd,
      ),
    );
    setSelectedNode(null);
    setShowNodeSettingsModal(false);
    setOriginalNodeConfig(null);
  }, [
    selectedNode,
    nodeLabel,
    cmdCommand,
    cmdTimeout,
    aiPrompt,
    aiProvider,
    aiTimeout,
    aiTools,
    aiRejectPattern,
    aiUseSession,
    aiSessionPlaceholder,
    startCreateWorktree,
    humanInputLabel,
    humanPrompt,
    promptNodePrompt,
    prDescriptionTemplate,
    adapterConfigValues,
    setNodes,
  ]);

  const handleCancelNodeSettings = useCallback(() => {
    if (originalNodeConfig) {
      setNodeLabel(originalNodeConfig.label);
      setCmdCommand(originalNodeConfig.cmdCommand);
      setCmdTimeout(originalNodeConfig.cmdTimeout);
      setAiPrompt(originalNodeConfig.aiPrompt);
      setAiProvider(originalNodeConfig.aiProvider);
      setAiTimeout(originalNodeConfig.aiTimeout);
      setAiTools(originalNodeConfig.aiTools);
      setAiRejectPattern(originalNodeConfig.aiRejectPattern);
      setAiUseSession(originalNodeConfig.aiUseSession);
      setAiSessionPlaceholder(originalNodeConfig.aiSessionPlaceholder);
      setStartCreateWorktree(originalNodeConfig.startCreateWorktree);
      setHumanInputLabel(originalNodeConfig.humanInputLabel);
      setHumanPrompt(originalNodeConfig.humanPrompt);
      setPromptNodePrompt(originalNodeConfig.promptNodePrompt);
      setAdapterConfigValues(originalNodeConfig.adapterConfigValues);
    }
    setSelectedNode(null);
    setShowNodeSettingsModal(false);
    setOriginalNodeConfig(null);
  }, [originalNodeConfig]);

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

  const onConnect = useCallback(
    (connection: Connection) => {
      const sourceNode = nodes.find((n) => n.id === connection.source);
      if (!sourceNode) return;

      const handleId = connection.sourceHandle;
      let edgeTypeFromHandle: EdgeType;
      if (handleId === "success") edgeTypeFromHandle = EdgeType.OnSuccess;
      else if (handleId === "fail") edgeTypeFromHandle = EdgeType.OnFailure;
      else if (handleId === "respond") edgeTypeFromHandle = EdgeType.OnRespond;
      else edgeTypeFromHandle = EdgeType.OnSuccess;

      const result = checkEdgeConstraints(
        connection.source,
        sourceNode.data?.type as NodeType,
        edges,
      );

      if (!result.allowed) {
        setEdgeError(result.error ?? "Cannot create edge");
        return;
      }

      if (
        edges.some((e) => e.source === connection.source && e.data?.edgeType === edgeTypeFromHandle)
      ) {
        setEdgeError("This edge type is already connected from this node");
        return;
      }

      setEdgeType(edgeTypeFromHandle);
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
      sourceHandle: pendingConnection.sourceHandle ?? "success",
      targetHandle: pendingConnection.targetHandle ?? "target-handle",
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
    setShowEdgeDeletePanel(true);
  }, []);

  const deleteSelectedEdge = useCallback(() => {
    if (!selectedEdge) return;
    setEdges((eds) => eds.filter((ed) => ed.id !== selectedEdge.id));
    setSelectedEdge(null);
    setShowEdgeDeletePanel(false);
  }, [selectedEdge, setEdges]);

  const cancelEdgeDelete = useCallback(() => {
    setSelectedEdge(null);
    setShowEdgeDeletePanel(false);
  }, []);

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if ((e.key === "Delete" || e.code === "Delete") && selectedEdge) {
        deleteSelectedEdge();
      }
    };

    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [selectedEdge, deleteSelectedEdge]);

  return (
    <div className="page-container">
      <ErrorBanner message={errorText} onDismiss={() => setErrorText("")} />
      <div className="loop-editor-header">
        <h1 className="page-title">Loop Editor</h1>
        {readOnlyVersion !== null && (
          <div className="readonly-banner" onClick={exitReadOnlyMode}>
            Viewing v{readOnlyVersion} (read-only) — click to exit
          </div>
        )}{" "}
        <div className="header-actions">
          {!isNarrow && (
            <button
              className="sidebar-toggle-btn"
              onClick={() => setSidebarVisible((v) => !v)}
              aria-label="Toggle sidebar"
            >
              {sidebarVisible ? "◀" : "▶"}
            </button>
          )}
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
            <button className="save-btn" onClick={handleSave} disabled={isSaving}>
              {isSaving ? "Saving…" : "Save"}
            </button>
          )}
          <button className="new-template-btn" onClick={handleNewTemplate}>
            New Template
          </button>
        </div>
      </div>

      <div className="loop-editor-layout">
        {(sidebarVisible || isNarrow) && (
          <>
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
                  <div className="loop-list-controls">
                    <label className="show-archived-toggle">
                      <input
                        type="checkbox"
                        checked={showArchived}
                        onChange={(e) => setShowArchived(e.target.checked)}
                      />
                      Show archived
                    </label>
                  </div>
                  {templates
                    .filter((t) => showArchived || !t.isArchived)
                    .map((template) => (
                      <div
                        key={template.id}
                        className={`loop-list-item ${selectedTemplate?.id === template.id ? "active" : ""} ${template.isArchived ? "archived" : ""}`}
                        onClick={() => selectTemplate(template)}
                      >
                        <div className="loop-list-item-name">
                          {template.name}{" "}
                          {template.isArchived && <span className="archived-badge">Archived</span>}
                        </div>
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
                              className="archive-btn"
                              onClick={(e) => {
                                e.stopPropagation();
                                void handleArchiveToggle(template);
                              }}
                            >
                              {template.isArchived ? "Unarchive" : "Archive"}
                            </button>
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
                        {new Date(v.createdAt).toLocaleDateString()} &middot; {v.nodeCount ?? "—"}{" "}
                        nodes
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </>
        )}

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
              onEdgesChange={onEdgesChangeCustom}
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

              {showNodeSettingsModal && selectedNode && (
                <div
                  className="node-settings-modal-overlay"
                  onClick={handleCancelNodeSettings}
                  role="dialog"
                  aria-modal="true"
                  aria-label="Node Settings"
                >
                  <div className="node-settings-modal" onClick={(e) => e.stopPropagation()}>
                    <div className="node-settings-modal-header">
                      <h2>Node Settings</h2>
                      <button
                        className="node-settings-modal-close"
                        onClick={handleCancelNodeSettings}
                        aria-label="Close"
                      >
                        ×
                      </button>
                    </div>
                    <div className="node-settings-modal-body">
                      <div className="config-field">
                        <label htmlFor="node-label">Label</label>
                        <input
                          id="node-label"
                          type="text"
                          value={nodeLabel}
                          onChange={(e) => setNodeLabel(e.target.value)}
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
                              onChange={(e) => setCmdCommand(e.target.value)}
                            />
                          </div>
                          <div className="config-field">
                            <label htmlFor="cmd-timeout">Timeout (seconds)</label>
                            <input
                              id="cmd-timeout"
                              type="number"
                              min={1}
                              value={cmdTimeout}
                              onChange={(e) => {
                                const val = parseInt(e.target.value, 10) || 30;
                                setCmdTimeout(val);
                              }}
                            />
                          </div>
                        </>
                      )}

                      {(selectedNode.data as { type: string }).type === NodeType.AI && (
                        <>
                          <div className="config-field">
                            <label htmlFor="ai-prompt">Prompt</label>
                            <PromptEditor
                              id="ai-prompt"
                              rows={4}
                              value={aiPrompt}
                              onChange={(v) => setAiPrompt(v)}
                            />
                          </div>
                          <div className="config-field">
                            <label className="checkbox-label">
                              <input
                                type="checkbox"
                                checked={aiUseSession}
                                onChange={(e) => setAiUseSession(e.target.checked)}
                              />
                              Use Session
                            </label>
                          </div>
                          {renderAiSessionControls()}
                          <div className="config-field">
                            <label htmlFor="ai-timeout">Timeout (seconds)</label>
                            <input
                              id="ai-timeout"
                              type="number"
                              min={1}
                              value={aiTimeout}
                              onChange={(e) => {
                                const val = parseInt(e.target.value, 10) || 300;
                                setAiTimeout(val);
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
                                const selected = aiProviders.find((p) => p.id === e.target.value);
                                if (selected) {
                                  void agentAdapterService
                                    .getConfigSchema(selected.type)
                                    .then((schema) => {
                                      setAdapterConfigSchema(schema);
                                      const initialValues: Record<
                                        string,
                                        string | number | boolean
                                      > = {};
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
                              onChange={(name, value) => {
                                setAdapterConfigValues((prev) => ({ ...prev, [name]: value }));
                              }}
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
                                    }}
                                  />
                                  {tool}
                                </label>
                              ))}
                            </div>
                          </div>
                          <div className="config-field">
                            <label htmlFor="ai-reject-pattern">Reject Pattern (regex)</label>
                            <input
                              id="ai-reject-pattern"
                              type="text"
                              value={aiRejectPattern}
                              onChange={(e) => setAiRejectPattern(e.target.value)}
                              placeholder="e.g. I cannot|I'm unable|REJECT"
                            />
                            <small
                              style={{
                                color: "#94a3b8",
                                marginTop: "0.25rem",
                                display: "block",
                              }}
                            >
                              If the AI output matches this pattern (case-insensitive), the node
                              fails and routes to the onFailure edge.
                            </small>
                          </div>
                        </>
                      )}

                      {(selectedNode.data as { type: string }).type === NodeType.Start && (
                        <div className="config-field">
                          <label className="checkbox-label">
                            <input
                              type="checkbox"
                              checked={startCreateWorktree}
                              onChange={(e) => setStartCreateWorktree(e.target.checked)}
                            />
                            Create worktree
                          </label>
                        </div>
                      )}

                      {(selectedNode.data as { type: string }).type === NodeType.Human && (
                        <>
                          <div className="config-field">
                            <label htmlFor="human-input-label">Input Label</label>
                            <input
                              id="human-input-label"
                              type="text"
                              value={humanInputLabel}
                              onChange={(e) => setHumanInputLabel(e.target.value)}
                            />
                          </div>
                          <div className="config-field">
                            <label htmlFor="human-prompt">Human Prompt</label>
                            <PromptEditor
                              id="human-prompt"
                              rows={6}
                              value={humanPrompt}
                              onChange={(v) => setHumanPrompt(v)}
                            />
                          </div>
                        </>
                      )}

                      {(selectedNode.data as { type: string }).type === NodeType.Prompt && (
                        <div className="config-field">
                          <label htmlFor="prompt-node-prompt">Prompt</label>
                          <PromptEditor
                            id="prompt-node-prompt"
                            rows={6}
                            value={promptNodePrompt}
                            onChange={(v) => setPromptNodePrompt(v)}
                          />
                        </div>
                      )}

                      {(selectedNode.data as { type: string }).type === NodeType.PR && (
                        <div className="config-field">
                          <label htmlFor="pr-description-template">PR Description Template</label>
                          <PromptEditor
                            id="pr-description-template"
                            rows={4}
                            value={prDescriptionTemplate}
                            onChange={(v) => setPrDescriptionTemplate(v)}
                          />
                        </div>
                      )}
                    </div>
                    <div className="node-settings-modal-footer">
                      <button className="node-settings-btn-delete" onClick={deleteSelectedNode}>
                        Delete Node
                      </button>
                      <div className="node-settings-footer-actions">
                        <button
                          className="node-settings-btn-cancel"
                          onClick={handleCancelNodeSettings}
                        >
                          Cancel
                        </button>
                        <button className="node-settings-btn-save" onClick={handleSaveNodeSettings}>
                          Save
                        </button>
                      </div>
                    </div>
                  </div>
                </div>
              )}

              {pendingConnection && (
                <Panel position="bottom-center" className="edge-config-panel">
                  <div className="config-panel-header">Configure Edge</div>
                  <div className="edge-type-indicator">
                    <span
                      className="edge-type-dot"
                      style={{
                        background:
                          edgeType === EdgeType.OnSuccess
                            ? "#10b981"
                            : edgeType === EdgeType.OnFailure
                              ? "#ef4444"
                              : "#f59e0b",
                      }}
                    />
                    <span>
                      {edgeType === EdgeType.OnSuccess
                        ? "success"
                        : edgeType === EdgeType.OnFailure
                          ? "failure"
                          : "respond"}
                    </span>
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

              {showEdgeDeletePanel && selectedEdge && (
                <Panel position="bottom-center" className="edge-delete-panel">
                  <div className="config-panel-header">Delete Edge</div>
                  <div className="edge-info-row">
                    <span className="edge-info-label">Type</span>
                    <span className="edge-info-value">
                      <span
                        className="edge-type-dot"
                        style={{
                          background:
                            selectedEdge.data?.edgeType === EdgeType.OnSuccess
                              ? "#10b981"
                              : selectedEdge.data?.edgeType === EdgeType.OnFailure
                                ? "#ef4444"
                                : "#f59e0b",
                        }}
                      />
                      {selectedEdge.data?.edgeType === EdgeType.OnSuccess
                        ? "success"
                        : selectedEdge.data?.edgeType === EdgeType.OnFailure
                          ? "failure"
                          : "respond"}
                    </span>
                  </div>
                  <div className="edge-info-row">
                    <span className="edge-info-label">From</span>
                    <span className="edge-info-value">
                      {(
                        nodes.find((n) => n.id === selectedEdge.source)?.data as {
                          label?: string;
                        }
                      )?.label ?? selectedEdge.source}
                    </span>
                  </div>
                  <div className="edge-info-row">
                    <span className="edge-info-label">To</span>
                    <span className="edge-info-value">
                      {(
                        nodes.find((n) => n.id === selectedEdge.target)?.data as {
                          label?: string;
                        }
                      )?.label ?? selectedEdge.target}
                    </span>
                  </div>
                  <div className="edge-config-actions">
                    <button className="delete-edge-btn" onClick={deleteSelectedEdge}>
                      Delete
                    </button>
                    <button className="cancel-edge-btn" onClick={cancelEdgeDelete}>
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
    </div>
  );
}
