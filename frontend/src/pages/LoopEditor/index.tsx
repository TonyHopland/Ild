import "./LoopEditor.css";
import { useState, useEffect, useCallback, useRef } from "react";
import {
  ReactFlow,
  Background,
  Controls,
  Panel,
  useNodesState,
  useEdgesState,
  type Node,
  type Edge,
  type Connection,
  type OnEdgesChange,
  applyEdgeChanges,
  addEdge,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { useMediaQuery } from "../../hooks/useMediaQuery";
import LoopNodeComponent from "../../components/LoopNodeComponent";
import ErrorBanner from "../../components/ErrorBanner";
import { loopTemplateService, agentAdapterService, aiProviderService } from "../../services/auth";
import {
  templateToNodes,
  templateToEdges,
  nodesToLoopNodes,
  edgesToLoopNodeEdges,
} from "../../utils/loopGraphConverter";
import { checkEdgeConstraints, buildEdge } from "../../utils/edgeUtils";
import {
  type AiProvider,
  type ConfigFieldDescriptor,
  EdgeType,
  type LoopTemplate,
  NodeType,
} from "../../types";
import { EdgePanels } from "./components/EdgePanels";
import { LoopEditorHeader } from "./components/LoopEditorHeader";
import { LoopEditorSidebar } from "./components/LoopEditorSidebar";
import { NodeSettingsModal } from "./components/NodeSettingsModal";
import type {
  AdapterConfigValue,
  LoopTemplateVersion,
  NodeSettingsSnapshot,
  SessionPlaceholderUsage,
} from "./types";

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

function collectSessionPlaceholderUsages(nodes: Node[]): SessionPlaceholderUsage[] {
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

function sanitizeAdapterConfigValues(
  adapterConfig: Record<string, unknown>,
): Record<string, AdapterConfigValue> {
  const values: Record<string, AdapterConfigValue> = {};

  for (const [name, value] of Object.entries(adapterConfig)) {
    if (typeof value === "string" || typeof value === "number" || typeof value === "boolean") {
      values[name] = value;
    }
  }

  return values;
}

export default function LoopEditor() {
  const [templates, setTemplates] = useState<LoopTemplate[]>([]);
  const [selectedTemplate, setSelectedTemplate] = useState<LoopTemplate | null>(null);
  const [nodes, setNodes, onNodesChange] = useNodesState<Node>([]);
  const [edges, setEdges] = useEdgesState<Edge>([]);
  const [selectedNode, setSelectedNode] = useState<Node | null>(null);
  const [selectedEdge, setSelectedEdge] = useState<Edge | null>(null);
  const [pendingConnection, setPendingConnection] = useState<Connection | null>(null);
  const [edgeType, setEdgeType] = useState<EdgeType>(EdgeType.OnSuccess);
  const [edgeMaxTraversals, setEdgeMaxTraversals] = useState("");
  const [edgeError, setEdgeError] = useState<string | null>(null);
  const [showEdgeDeletePanel, setShowEdgeDeletePanel] = useState(false);
  const [showNodeSettingsModal, setShowNodeSettingsModal] = useState(false);
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
  const [saveSuccess, setSaveSuccess] = useState(false);
  const [validationErrors, setValidationErrors] = useState<string[]>([]);
  const [isNewTemplate, setIsNewTemplate] = useState(false);
  const [newTemplateName, setNewTemplateName] = useState("");
  const [cloningTemplateId, setCloningTemplateId] = useState<string | null>(null);
  const [cloneName, setCloneName] = useState("");
  const [showVersionHistory, setShowVersionHistory] = useState(false);
  const [versionHistory, setVersionHistory] = useState<LoopTemplateVersion[]>([]);
  const [readOnlyVersion, setReadOnlyVersion] = useState<number | null>(null);
  const [adapterConfigSchema, setAdapterConfigSchema] = useState<ConfigFieldDescriptor[]>([]);
  const [adapterConfigValues, setAdapterConfigValues] = useState<
    Record<string, AdapterConfigValue>
  >({});
  const [aiProviders, setAiProviders] = useState<AiProvider[]>([]);
  const [errorText, setErrorText] = useState("");
  const [isSaving, setIsSaving] = useState(false);
  const [sidebarVisible, setSidebarVisible] = useState(true);
  const [showArchived, setShowArchived] = useState(false);
  const [originalNodeConfig, setOriginalNodeConfig] = useState<NodeSettingsSnapshot | null>(null);

  const reactFlowWrapper = useRef<HTMLDivElement>(null);
  const reactFlowInstance = useRef<any>(null);
  const saveTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const isNarrow = useMediaQuery("(max-width: 900px)");

  const sessionPlaceholderUsages = collectSessionPlaceholderUsages(nodes);
  const selectedPlaceholderUsage = sessionPlaceholderUsages.find(
    (entry) => entry.name === aiSessionPlaceholder.trim(),
  );

  const loadAdapterSchema = useCallback(
    async (providerId: string, initialAdapterConfig: Record<string, unknown> = {}) => {
      const selectedProvider = aiProviders.find((provider) => provider.id === providerId);
      if (!selectedProvider) {
        setAdapterConfigSchema([]);
        setAdapterConfigValues({});
        return;
      }

      const schema = await agentAdapterService.getConfigSchema(selectedProvider.type);
      const nextValues: Record<string, AdapterConfigValue> = {};
      for (const field of schema) {
        const nodeValue = initialAdapterConfig[field.name];
        if (
          typeof nodeValue === "string" ||
          typeof nodeValue === "number" ||
          typeof nodeValue === "boolean"
        ) {
          nextValues[field.name] = nodeValue;
        } else if (field.defaultValue !== null && field.defaultValue !== undefined) {
          nextValues[field.name] = field.defaultValue as AdapterConfigValue;
        }
      }

      setAdapterConfigSchema(schema);
      setAdapterConfigValues(nextValues);
    },
    [aiProviders],
  );

  useEffect(() => {
    void loadTemplates();
    void loadAiProviders();
  }, []);

  useEffect(() => {
    return () => {
      if (saveTimeoutRef.current) clearTimeout(saveTimeoutRef.current);
    };
  }, []);

  const onEdgesChangeCustom: OnEdgesChange = useCallback(
    (changes) => {
      setEdges((previousEdges) => {
        const nextEdges = applyEdgeChanges(changes, previousEdges);
        return nextEdges.map((edge) => {
          const previousEdge = previousEdges.find((candidate) => candidate.id === edge.id);
          if (!previousEdge) return edge;

          let restoredEdge = edge;
          if (previousEdge.data && !edge.data) {
            restoredEdge = { ...restoredEdge, data: previousEdge.data };
          }
          if (previousEdge.sourceHandle && !edge.sourceHandle) {
            restoredEdge = { ...restoredEdge, sourceHandle: previousEdge.sourceHandle };
          }
          if (previousEdge.targetHandle && !edge.targetHandle) {
            restoredEdge = { ...restoredEdge, targetHandle: previousEdge.targetHandle };
          }
          return restoredEdge;
        });
      });
    },
    [setEdges],
  );

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

  const selectTemplate = useCallback(
    (template: LoopTemplate) => {
      setSelectedTemplate(template);
      setNodes(templateToNodes(template));
      setEdges(templateToEdges(template));
      setValidationErrors([]);
      setSaveSuccess(false);
      setIsNewTemplate(false);
      setNewTemplateName("");
      setReadOnlyVersion(null);
    },
    [setEdges, setNodes],
  );

  const handleNewTemplate = () => {
    setSelectedTemplate(null);
    setNodes([]);
    setEdges([]);
    setValidationErrors([]);
    setSaveSuccess(false);
    setIsNewTemplate(true);
    setNewTemplateName("");
    setReadOnlyVersion(null);
  };

  const handleSave = async () => {
    if (isSaving) return;

    if (nodes.length === 0) {
      setValidationErrors(["Graph must contain at least one node."]);
      return;
    }

    const errors: string[] = [];
    const currentNodeTypes = nodes.map((node) => (node.data as { type?: string }).type);

    if (!currentNodeTypes.includes(NodeType.Start)) {
      errors.push("Graph must contain a Start node.");
    }
    if (!currentNodeTypes.includes(NodeType.Cleanup)) {
      errors.push("Graph must contain a Cleanup node.");
    }

    const startNode = nodes.find(
      (node) => (node.data as { type?: string }).type === NodeType.Start,
    );
    if (startNode) {
      const adjacency = new Map<string, string[]>();
      for (const edge of edges) {
        const targets = adjacency.get(edge.source) ?? [];
        targets.push(edge.target);
        adjacency.set(edge.source, targets);
      }

      const reachable = new Set<string>();
      const queue: string[] = [startNode.id];
      reachable.add(startNode.id);
      while (queue.length > 0) {
        const current = queue.shift()!;
        for (const target of adjacency.get(current) ?? []) {
          if (!reachable.has(target)) {
            reachable.add(target);
            queue.push(target);
          }
        }
      }

      const unreachableNodes = nodes
        .filter((node) => !reachable.has(node.id))
        .map((node) => node.id);
      if (unreachableNodes.length > 0) {
        errors.push(`Unreachable nodes from Start: ${unreachableNodes.join(", ")}`);
      }

      const cleanupNodes = nodes.filter(
        (node) => (node.data as { type?: string }).type === NodeType.Cleanup,
      );
      const reachableCleanup = cleanupNodes.some((node) => reachable.has(node.id));
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
          setReadOnlyVersion(null);
        }
      }
      void loadTemplates();
    } catch (error) {
      setErrorText(loadErrorMessage(error, "Failed to update template."));
    }
  };

  const handleShowVersionHistory = async (template: LoopTemplate) => {
    try {
      const versions = (await loopTemplateService.getVersions(
        template.id,
      )) as LoopTemplateVersion[];
      setVersionHistory(versions);
      setShowVersionHistory(true);
    } catch (error) {
      setErrorText(loadErrorMessage(error, "Failed to load versions."));
    }
  };

  const handleSelectVersion = useCallback(
    async (loopTemplateId: string, versionNumber: number) => {
      try {
        const version = await loopTemplateService.getById(
          `${loopTemplateId}/versions/${versionNumber}`,
        );
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
    },
    [setEdges, setNodes],
  );

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
      if (!nodeType || !reactFlowWrapper.current || !reactFlowInstance.current) return;

      const bounds = reactFlowWrapper.current.getBoundingClientRect();
      const position = reactFlowInstance.current.screenToFlowPosition({
        x: event.clientX - bounds.left,
        y: event.clientY - bounds.top,
      });

      const newNode: Node = {
        id: `node-${Date.now()}`,
        type: "loopNode",
        position,
        data: {
          label: nodeType,
          type: nodeType,
        },
      };

      setNodes((currentNodes) => currentNodes.concat(newNode));
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
      setSelectedEdge(null);
      setShowEdgeDeletePanel(false);

      const data = node.data as {
        label: string;
        type: string;
        config?: Record<string, unknown>;
      };
      const config = data.config || {};
      const adapterConfig = (config.adapterConfig as Record<string, unknown>) || {};
      const initialAdapterValues = sanitizeAdapterConfigValues(adapterConfig);

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
      setAdapterConfigValues(initialAdapterValues);

      if (data.type === NodeType.AI) {
        void loadAdapterSchema((config.aiProviderId as string) || "", adapterConfig);
      } else {
        setAdapterConfigSchema([]);
        setAdapterConfigValues({});
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
        prDescriptionTemplate: (config.prDescriptionTemplate as string) || "",
        adapterConfigValues: initialAdapterValues,
      });
      setShowNodeSettingsModal(true);
    },
    [loadAdapterSchema],
  );

  const handleAiProviderChange = useCallback(
    (providerId: string) => {
      setAiProvider(providerId);
      void loadAdapterSchema(providerId);
    },
    [loadAdapterSchema],
  );

  const handleSaveNodeSettings = useCallback(() => {
    if (!selectedNode) return;
    const selectedNodeType = (selectedNode.data as { type: string }).type;

    if (selectedNodeType === NodeType.AI && aiUseSession && !aiSessionPlaceholder.trim()) {
      setErrorText("AI nodes with Use Session enabled must set a session placeholder.");
      return;
    }

    const config: Record<string, unknown> = {};
    if (selectedNodeType === NodeType.Cmd) {
      config.command = cmdCommand;
      config.timeout = cmdTimeout;
    } else if (selectedNodeType === NodeType.AI) {
      config.prompt = aiPrompt;
      config.useSession = aiUseSession;
      config.aiProviderId = aiProvider;
      config.timeout = aiTimeout;
      config.toolAllowlist = aiTools;
      config.adapterConfig = { ...adapterConfigValues };
      if (aiRejectPattern) config.rejectPattern = aiRejectPattern;
      config.sessionPlaceholder = aiUseSession ? aiSessionPlaceholder.trim() : undefined;
    } else if (selectedNodeType === NodeType.Start) {
      config.createWorktree = startCreateWorktree;
    } else if (selectedNodeType === NodeType.Human) {
      config.inputLabel = humanInputLabel;
      if (humanPrompt) config.prompt = humanPrompt;
    } else if (selectedNodeType === NodeType.Prompt) {
      if (promptNodePrompt) config.prompt = promptNodePrompt;
    } else if (selectedNodeType === NodeType.PR) {
      if (prDescriptionTemplate) config.prDescriptionTemplate = prDescriptionTemplate;
    }

    setNodes((currentNodes) =>
      currentNodes.map((node) =>
        node.id === selectedNode.id
          ? {
              ...node,
              data: {
                ...node.data,
                label: nodeLabel,
                config: {
                  ...(node.data as { config?: Record<string, unknown> }).config,
                  ...config,
                },
              },
            }
          : node,
      ),
    );

    setSelectedNode(null);
    setShowNodeSettingsModal(false);
    setOriginalNodeConfig(null);
  }, [
    selectedNode,
    aiProvider,
    aiPrompt,
    aiRejectPattern,
    aiSessionPlaceholder,
    aiTimeout,
    aiTools,
    aiUseSession,
    adapterConfigValues,
    cmdCommand,
    cmdTimeout,
    humanInputLabel,
    humanPrompt,
    nodeLabel,
    prDescriptionTemplate,
    promptNodePrompt,
    setNodes,
    startCreateWorktree,
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
      setPrDescriptionTemplate(originalNodeConfig.prDescriptionTemplate);
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
        (node) =>
          node.id !== selectedNode?.id &&
          (node.data as { label?: string }).label?.trim() === label.trim(),
      );
      if (duplicates.length > 0) {
        setLabelError("Label must be unique");
        return false;
      }

      setLabelError(null);
      return true;
    },
    [nodes, selectedNode],
  );

  const deleteSelectedNode = useCallback(() => {
    if (!selectedNode) return;
    setNodes((currentNodes) => currentNodes.filter((node) => node.id !== selectedNode.id));
    setEdges((currentEdges) =>
      currentEdges.filter(
        (edge) => edge.source !== selectedNode.id && edge.target !== selectedNode.id,
      ),
    );
    setSelectedNode(null);
    setShowNodeSettingsModal(false);
  }, [selectedNode, setEdges, setNodes]);

  const onConnect = useCallback(
    (connection: Connection) => {
      const sourceNode = nodes.find((node) => node.id === connection.source);
      if (!sourceNode) return;

      let nextEdgeType = EdgeType.OnSuccess;
      if (connection.sourceHandle === "fail") nextEdgeType = EdgeType.OnFailure;
      if (connection.sourceHandle === "respond") nextEdgeType = EdgeType.OnRespond;

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
        edges.some(
          (edge) => edge.source === connection.source && edge.data?.edgeType === nextEdgeType,
        )
      ) {
        setEdgeError("This edge type is already connected from this node");
        return;
      }

      setEdgeType(nextEdgeType);
      setEdgeMaxTraversals("");
      setEdgeError(null);
      setPendingConnection(connection);
    },
    [edges, nodes],
  );

  const confirmEdge = useCallback(() => {
    if (!pendingConnection) return;

    if (
      edgeMaxTraversals !== "" &&
      (Number.isNaN(Number(edgeMaxTraversals)) || Number(edgeMaxTraversals) < 0)
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

    setEdges((currentEdges) => addEdge(newEdge, currentEdges));
    setPendingConnection(null);
    setEdgeMaxTraversals("");
    setEdgeError(null);
  }, [edgeMaxTraversals, edgeType, pendingConnection, setEdges]);

  const cancelEdge = useCallback(() => {
    setPendingConnection(null);
    setEdgeMaxTraversals("");
    setEdgeError(null);
    setSelectedEdge(null);
    setShowEdgeDeletePanel(false);
  }, []);

  const onEdgeClick = useCallback((_event: React.MouseEvent, edge: Edge) => {
    setSelectedEdge(edge);
    setSelectedNode(null);
    setShowNodeSettingsModal(false);
    setShowEdgeDeletePanel(true);
  }, []);

  const deleteSelectedEdge = useCallback(() => {
    if (!selectedEdge) return;
    setEdges((currentEdges) => currentEdges.filter((edge) => edge.id !== selectedEdge.id));
    setSelectedEdge(null);
    setShowEdgeDeletePanel(false);
  }, [selectedEdge, setEdges]);

  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if ((event.key === "Delete" || event.code === "Delete") && selectedEdge) {
        deleteSelectedEdge();
      }
    };

    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [deleteSelectedEdge, selectedEdge]);

  const onPaletteDragStart = useCallback(
    (nodeType: NodeType, event: React.DragEvent<HTMLDivElement>) => {
      event.dataTransfer.setData("application/loop-node-type", nodeType);
    },
    [],
  );

  const selectedTemplateName = selectedTemplate?.name || newTemplateName || "Untitled";

  return (
    <div className="page-container">
      <ErrorBanner message={errorText} onDismiss={() => setErrorText("")} />

      <LoopEditorHeader
        isNarrow={isNarrow}
        sidebarVisible={sidebarVisible}
        isNewTemplate={isNewTemplate}
        newTemplateName={newTemplateName}
        saveSuccess={saveSuccess}
        canSave={Boolean(selectedTemplate || isNewTemplate)}
        isSaving={isSaving}
        readOnlyVersion={readOnlyVersion}
        onToggleSidebar={() => setSidebarVisible((visible) => !visible)}
        onExitReadOnlyMode={exitReadOnlyMode}
        onNewTemplateNameChange={setNewTemplateName}
        onSave={handleSave}
        onCreateTemplate={handleNewTemplate}
      />

      <div className="loop-editor-layout">
        {(sidebarVisible || isNarrow) && (
          <LoopEditorSidebar
            readOnlyVersion={readOnlyVersion}
            paletteItems={paletteItems}
            showArchived={showArchived}
            templates={templates}
            selectedTemplateId={selectedTemplate?.id ?? null}
            cloningTemplateId={cloningTemplateId}
            cloneName={cloneName}
            showVersionHistory={showVersionHistory}
            versionHistory={versionHistory}
            onPaletteDragStart={onPaletteDragStart}
            onShowArchivedChange={setShowArchived}
            onSelectTemplate={selectTemplate}
            onStartClone={(template) => {
              setCloningTemplateId(template.id);
              setCloneName(`Copy of ${template.name}`);
            }}
            onCloneNameChange={setCloneName}
            onConfirmClone={(template) => void handleClone(template)}
            onToggleArchive={(template) => void handleArchiveToggle(template)}
            onShowVersionHistory={(template) => void handleShowVersionHistory(template)}
            onBackToTemplates={() => setShowVersionHistory(false)}
            onSelectVersion={(loopTemplateId, versionNumber) =>
              void handleSelectVersion(loopTemplateId, versionNumber)
            }
          />
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
                <span>{selectedTemplateName}</span>
                <span>v{selectedTemplate?.version ?? "new"}</span>
              </Panel>

              {validationErrors.length > 0 && (
                <Panel position="top-center" className="validation-errors-panel">
                  <div className="validation-errors-header">Validation Errors</div>
                  {validationErrors.map((error, index) => (
                    <div key={index} className="validation-error-badge">
                      {error}
                    </div>
                  ))}
                </Panel>
              )}

              {showNodeSettingsModal && selectedNode && (
                <NodeSettingsModal
                  selectedNode={selectedNode}
                  labelError={labelError}
                  nodeLabel={nodeLabel}
                  cmdCommand={cmdCommand}
                  cmdTimeout={cmdTimeout}
                  aiPrompt={aiPrompt}
                  aiProvider={aiProvider}
                  aiTimeout={aiTimeout}
                  aiTools={aiTools}
                  aiRejectPattern={aiRejectPattern}
                  aiUseSession={aiUseSession}
                  aiSessionPlaceholder={aiSessionPlaceholder}
                  startCreateWorktree={startCreateWorktree}
                  humanInputLabel={humanInputLabel}
                  humanPrompt={humanPrompt}
                  promptNodePrompt={promptNodePrompt}
                  prDescriptionTemplate={prDescriptionTemplate}
                  aiProviders={aiProviders}
                  adapterConfigSchema={adapterConfigSchema}
                  adapterConfigValues={adapterConfigValues}
                  sessionPlaceholderUsages={sessionPlaceholderUsages}
                  selectedPlaceholderUsage={selectedPlaceholderUsage}
                  onClose={handleCancelNodeSettings}
                  onDeleteNode={deleteSelectedNode}
                  onSave={handleSaveNodeSettings}
                  onValidateLabel={validateLabel}
                  onNodeLabelChange={setNodeLabel}
                  onCmdCommandChange={setCmdCommand}
                  onCmdTimeoutChange={setCmdTimeout}
                  onAiPromptChange={setAiPrompt}
                  onAiProviderChange={handleAiProviderChange}
                  onAiTimeoutChange={setAiTimeout}
                  onAiToolsChange={setAiTools}
                  onAiRejectPatternChange={setAiRejectPattern}
                  onAiUseSessionChange={setAiUseSession}
                  onAiSessionPlaceholderChange={setAiSessionPlaceholder}
                  onStartCreateWorktreeChange={setStartCreateWorktree}
                  onHumanInputLabelChange={setHumanInputLabel}
                  onHumanPromptChange={setHumanPrompt}
                  onPromptNodePromptChange={setPromptNodePrompt}
                  onPrDescriptionTemplateChange={setPrDescriptionTemplate}
                  onAdapterConfigChange={(name, value) =>
                    setAdapterConfigValues((current) => ({ ...current, [name]: value }))
                  }
                />
              )}

              <EdgePanels
                pendingConnection={pendingConnection !== null}
                edgeType={edgeType}
                edgeMaxTraversals={edgeMaxTraversals}
                edgeError={edgeError}
                showEdgeDeletePanel={showEdgeDeletePanel}
                selectedEdge={selectedEdge}
                nodes={nodes}
                onEdgeMaxTraversalsChange={setEdgeMaxTraversals}
                onConfirmEdge={confirmEdge}
                onCancelEdge={cancelEdge}
                onDeleteEdge={deleteSelectedEdge}
              />
            </ReactFlow>
          ) : (
            <div className="loop-canvas-empty">Select a template to view its graph</div>
          )}
        </div>
      </div>
    </div>
  );
}
