import "./LoopEditor.css";
import { useState, useEffect, useCallback, useRef, useMemo } from "react";
import { useNavigate, useParams } from "react-router-dom";
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
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { useMediaQuery } from "../../hooks/useMediaQuery";
import LoopNodeComponent from "../../components/LoopNodeComponent";
import LoopEdgeComponent from "../../components/LoopEdgeComponent";
import { LoopEdgeInteractionContext } from "../../components/loopEdgeInteraction";
import ErrorBanner from "../../components/ErrorBanner";
import { loopTemplateService, agentAdapterService, aiProviderService } from "../../services/auth";
import {
  templateToNodes,
  templateToEdges,
  nodesToLoopNodes,
  edgesToLoopNodeEdges,
} from "../../utils/loopGraphConverter";
import {
  serializeForExport,
  downloadExport,
  parseImportFile,
  exportNodesToLoopNodes,
  exportEdgesToLoopNodeEdges,
} from "../../utils/loopTemplateExport";
import {
  checkEdgeConstraints,
  buildEdge,
  appendEdge,
  getCustomEdgeNames,
  getConnectedCustomEdgeNames,
  LOOP_EDGE_TYPE,
} from "../../utils/edgeUtils";
import {
  type AiMatchRule,
  type AiToolDefinition,
  type AiProvider,
  type ConfigFieldDescriptor,
  type LoopNode,
  type LoopNodeEdge,
  type LoopTemplate,
  EdgeType,
  NodeType,
  RecoveryPolicy,
} from "../../types";
import { EdgePanels } from "./components/EdgePanels";
import { LoopEditorHeader } from "./components/LoopEditorHeader";
import { LoopEditorSidebar } from "./components/LoopEditorSidebar";
import { NodeSettingsModal } from "./components/NodeSettingsModal";
import type {
  AdapterConfigValue,
  ImportFeedbackItem,
  LoopTemplateVersion,
  NodeSettingsSnapshot,
  SessionPlaceholderUsage,
} from "./types";
import { validateLoopGraphLocally } from "./utils/loopGraphValidation";

function loadErrorMessage(error: unknown, fallback: string): string {
  if (error instanceof Error && error.message) return error.message;
  if (typeof error === "string") return error;
  return fallback;
}

const nodeTypes = {
  loopNode: LoopNodeComponent,
};

const edgeTypes = {
  [LOOP_EDGE_TYPE]: LoopEdgeComponent,
};

const paletteItems = [
  { type: NodeType.Start, label: "Start" },
  { type: NodeType.Cmd, label: "Cmd" },
  { type: NodeType.AI, label: "AI" },
  { type: NodeType.Human, label: "Human" },
  { type: NodeType.Prompt, label: "Prompt" },
  { type: NodeType.PR, label: "PR" },
  { type: NodeType.Condition, label: "Condition" },
  { type: NodeType.Cleanup, label: "Cleanup" },
];

/** Pass-through default for a Condition node's Subject and Output templates. */
const CONDITION_DEFAULT_TEMPLATE = "{{Node.Input}}";
/** A new Condition node starts on the TextMatches variant. */
const CONDITION_DEFAULT_VARIANT = "TextMatches";

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

function resolveActiveAiProvider(
  aiProviders: AiProvider[] | null | undefined,
  providerId: string,
): AiProvider | null {
  const providers = Array.isArray(aiProviders) ? aiProviders : [];
  return (
    providers.find((provider) => provider.id === providerId) ??
    providers.find((provider) => provider.isDefault) ??
    providers[0] ??
    null
  );
}

function resolveToolSelection(provider: AiProvider | null, configuredTools: unknown): string[] {
  const supportedTools = provider?.supportedTools ?? [];
  if (supportedTools.length === 0) return [];

  const supportedToolKeys = new Set(supportedTools.map((tool) => tool.key));
  const explicitTools = Array.isArray(configuredTools)
    ? configuredTools.filter(
        (tool): tool is string => typeof tool === "string" && supportedToolKeys.has(tool),
      )
    : [];

  if (explicitTools.length > 0) return explicitTools;

  return supportedTools.filter((tool) => tool.defaultEnabled).map((tool) => tool.key);
}

/** Reads an AI node's ordered output-match rules from its config. */
function readMatchRules(config: Record<string, unknown>): AiMatchRule[] {
  const rules = config.matchRules;
  if (!Array.isArray(rules)) return [];
  return rules
    .map((rule) => ({
      pattern: String((rule as AiMatchRule)?.pattern ?? ""),
      edgeName: String((rule as AiMatchRule)?.edgeName ?? ""),
    }))
    .filter((rule) => rule.pattern !== "" || rule.edgeName !== "");
}

/** Reads a Human/PR node's declared custom edge names from its config. */
function readCustomEdges(config: Record<string, unknown>): string[] {
  const names = config.customEdges;
  if (!Array.isArray(names)) return [];
  return names.filter((name): name is string => typeof name === "string");
}

/** Trims, drops blanks and dedupes a node's custom edge names for persistence. */
function cleanCustomEdgeNames(names: string[]): string[] {
  const seen = new Set<string>();
  for (const name of names) {
    const trimmed = name.trim();
    if (trimmed) seen.add(trimmed);
  }
  return [...seen];
}

export default function LoopEditor() {
  const navigate = useNavigate();
  const { templateId: routeTemplateId } = useParams<{ templateId?: string }>();
  const [templates, setTemplates] = useState<LoopTemplate[]>([]);
  const [templatesLoaded, setTemplatesLoaded] = useState(false);
  const [selectedTemplate, setSelectedTemplate] = useState<LoopTemplate | null>(null);
  const [nodes, setNodes, onNodesChange] = useNodesState<Node>([]);
  const [edges, setEdges] = useEdgesState<Edge>([]);
  const nodesRef = useRef<Node[]>([]);
  const edgesRef = useRef<Edge[]>([]);

  // Keep refs in sync so handleExport reads current graph without stale closures
  useEffect(() => {
    nodesRef.current = nodes;
    edgesRef.current = edges;
  }, [nodes, edges]);
  const [selectedNode, setSelectedNode] = useState<Node | null>(null);
  const [selectedEdge, setSelectedEdge] = useState<Edge | null>(null);
  const [pendingConnection, setPendingConnection] = useState<Connection | null>(null);
  const [edgeType, setEdgeType] = useState<EdgeType>(EdgeType.OnSuccess);
  const [edgeName, setEdgeName] = useState("");
  const [edgeMaxTraversals, setEdgeMaxTraversals] = useState("");
  const [edgeError, setEdgeError] = useState<string | null>(null);
  const [showEdgeDeletePanel, setShowEdgeDeletePanel] = useState(false);
  const [showNodeSettingsModal, setShowNodeSettingsModal] = useState(false);
  const [nodeLabel, setNodeLabel] = useState("");
  const [cmdCommand, setCmdCommand] = useState("");
  const [aiPrompt, setAiPrompt] = useState("");
  const [aiProvider, setAiProvider] = useState("");
  const [aiTools, setAiTools] = useState<string[]>([]);
  const [aiMatchRules, setAiMatchRules] = useState<AiMatchRule[]>([]);
  const [customEdgeNames, setCustomEdgeNames] = useState<string[]>([]);
  const [aiUseSession, setAiUseSession] = useState(false);
  const [aiSessionPlaceholder, setAiSessionPlaceholder] = useState("");
  const [aiForkFromPlaceholder, setAiForkFromPlaceholder] = useState("");
  const [startCreateWorktree, setStartCreateWorktree] = useState(true);
  const [startRunInstall, setStartRunInstall] = useState(false);
  const [humanInputLabel, setHumanInputLabel] = useState("");
  const [humanPrompt, setHumanPrompt] = useState("");
  const [promptNodePrompt, setPromptNodePrompt] = useState("");
  const [prDescriptionTemplate, setPrDescriptionTemplate] = useState("");
  const [prCommentTemplate, setPrCommentTemplate] = useState("");
  const [conditionVariant, setConditionVariant] = useState(CONDITION_DEFAULT_VARIANT);
  const [conditionSubject, setConditionSubject] = useState("");
  const [conditionPattern, setConditionPattern] = useState("");
  const [conditionTag, setConditionTag] = useState("");
  const [conditionOutput, setConditionOutput] = useState(CONDITION_DEFAULT_TEMPLATE);
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

  // Import state
  const [importFeedback, setImportFeedback] = useState<ImportFeedbackItem[]>([]);
  const [showImportFeedback, setShowImportFeedback] = useState(false);
  const [importConflictTemplate, setImportConflictTemplate] = useState<LoopTemplate | null>(null);
  const [importConflictData, setImportConflictData] = useState<{
    filename: string;
    name: string;
    description: string;
    recoveryPolicy: RecoveryPolicy;
    nodes: LoopNode[];
    edges: LoopNodeEdge[];
  } | null>(null);

  // Refs for import queue — avoids window pollution and stale closures
  const importQueueRef = useRef<{
    remainingFiles: File[];
    feedback: ImportFeedbackItem[];
  } | null>(null);
  const templatesRef = useRef<LoopTemplate[]>([]);

  const reactFlowWrapper = useRef<HTMLDivElement>(null);
  const reactFlowInstance = useRef<any>(null);
  const saveTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const importFileInputRef = useRef<HTMLInputElement>(null);
  const isNarrow = useMediaQuery("(max-width: 900px)");

  const sessionPlaceholderUsages = collectSessionPlaceholderUsages(nodes);
  const selectedPlaceholderUsage = sessionPlaceholderUsages.find(
    (entry) => entry.name === aiSessionPlaceholder.trim(),
  );
  const availableAiTools: AiToolDefinition[] = useMemo(
    () => resolveActiveAiProvider(aiProviders, aiProvider)?.supportedTools ?? [],
    [aiProviders, aiProvider],
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

  const loadTemplates = useCallback(async () => {
    try {
      const data = await loopTemplateService.getAll({ includeArchived: true });
      setTemplates(data);
      templatesRef.current = data;
    } catch (error) {
      setErrorText(loadErrorMessage(error, "Failed to load loop templates."));
    } finally {
      setTemplatesLoaded(true);
    }
  }, []);

  const loadAiProviders = async () => {
    try {
      const data = await aiProviderService.getAll();
      setAiProviders(Array.isArray(data) ? data : []);
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

  // The URL is the source of truth for which template is open, so a link to
  // `/loop-editor/<id>` opens that template's graph directly. Tracking the id we
  // last opened keeps an unrelated `templates` refresh (save, clone, archive)
  // from re-loading — and clobbering — the canvas the user is editing.
  const openedTemplateIdRef = useRef<string | null>(null);
  useEffect(() => {
    if (!routeTemplateId) {
      openedTemplateIdRef.current = null;
      return;
    }
    if (openedTemplateIdRef.current === routeTemplateId) return;

    const match = templates.find((template) => template.id === routeTemplateId);
    if (match) {
      openedTemplateIdRef.current = routeTemplateId;
      selectTemplate(match);
      return;
    }
    // A link to a template that no longer exists bounces back to the bare editor
    // so the URL always matches what is actually open.
    if (templatesLoaded) {
      void navigate("/loop-editor", { replace: true });
    }
  }, [routeTemplateId, templates, templatesLoaded, selectTemplate, navigate]);

  const handleNewTemplate = () => {
    setSelectedTemplate(null);
    setNodes([]);
    setEdges([]);
    setValidationErrors([]);
    setSaveSuccess(false);
    setIsNewTemplate(true);
    setNewTemplateName("");
    setReadOnlyVersion(null);
    if (routeTemplateId) void navigate("/loop-editor");
  };

  const handleSave = async () => {
    if (isSaving) return;

    const errors = validateLoopGraphLocally(nodes, edges);
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
          recoveryPolicy: RecoveryPolicy.AutoResume,
          nodes: loopNodes,
          edges: loopEdges,
        });
      } else if (selectedTemplate) {
        await loopTemplateService.update(selectedTemplate.id, {
          name: selectedTemplate.name,
          description: selectedTemplate.description,
          recoveryPolicy: selectedTemplate.recoveryPolicy,
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
          if (routeTemplateId) void navigate("/loop-editor");
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
    if (routeTemplateId) void navigate("/loop-editor");
  };

  // --- Export ---
  // Issue #5: nodes/edges change on every React Flow interaction, so we read
  // them from refs inside the handler instead of capturing in useCallback deps.
  const handleExport = useCallback(() => {
    // Read current graph from refs to avoid stale closures
    const loopNodes = nodesToLoopNodes(nodesRef.current);
    const loopEdges = edgesToLoopNodeEdges(edgesRef.current);

    const exportTemplate: LoopTemplate = selectedTemplate
      ? {
          ...selectedTemplate,
          nodes: loopNodes,
          edges: loopEdges,
        }
      : {
          id: "",
          name: newTemplateName || "Untitled",
          description: "",
          version: 0,
          recoveryPolicy: RecoveryPolicy.AutoResume,
          nodes: loopNodes,
          edges: loopEdges,
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
          isArchived: false,
        };

    const exportData = serializeForExport(exportTemplate);
    downloadExport(exportData, exportTemplate.name);
  }, [selectedTemplate, newTemplateName]);

  // --- Import ---
  // Issue #1: useRef instead of window for import queue
  // Issue #2: templatesRef instead of templates to avoid stale closures
  // Issue #3: LoopNode[]/LoopNodeEdge[] instead of unknown[]
  // Issue #4: discriminated union from parseImportFile
  // Issue #6: store original filename in conflict data

  const processSingleImportFile = useCallback(
    async (
      file: File,
      accumulatedFeedback: ImportFeedbackItem[],
    ): Promise<{
      feedback: ImportFeedbackItem[];
      haltedForConflict: boolean;
    }> => {
      try {
        const raw = await file.text();
        const result = parseImportFile(raw);

        if (!result.ok) {
          accumulatedFeedback.push({
            filename: file.name,
            status: "error",
            message: result.error,
          });
          return { feedback: accumulatedFeedback, haltedForConflict: false };
        }

        const parsed = result.data;

        // Validate graph via API
        const loopNodes = exportNodesToLoopNodes(parsed.nodes);
        const loopEdges = exportEdgesToLoopNodeEdges(parsed.edges);
        const validationResult = await loopTemplateService.validate({
          nodes: loopNodes,
          edges: loopEdges,
        });

        if (!validationResult.valid) {
          accumulatedFeedback.push({
            filename: file.name,
            status: "error",
            message: `Graph validation failed: ${validationResult.errors.join(", ")}`,
          });
          return { feedback: accumulatedFeedback, haltedForConflict: false };
        }

        // Check for name conflicts — use ref to get fresh data
        const existing = templatesRef.current.find(
          (t) => t.name.toLowerCase() === parsed.name.toLowerCase(),
        );

        if (existing) {
          // Conflict — store in state for dialog
          setImportConflictTemplate(existing);
          setImportConflictData({
            filename: file.name, // Issue #6: store original filename
            name: parsed.name,
            description: parsed.description,
            recoveryPolicy: parsed.recoveryPolicy,
            nodes: loopNodes,
            edges: loopEdges,
          });
          return { feedback: accumulatedFeedback, haltedForConflict: true };
        }

        // No conflict — create new template
        await loopTemplateService.create({
          name: parsed.name,
          description: parsed.description,
          recoveryPolicy: parsed.recoveryPolicy,
          nodes: loopNodes,
          edges: loopEdges,
        });

        accumulatedFeedback.push({
          filename: file.name,
          status: "success",
          message: `Created "${parsed.name}" successfully.`,
        });
      } catch (err) {
        accumulatedFeedback.push({
          filename: file.name,
          status: "error",
          message: loadErrorMessage(err, "Failed to import."),
        });
      }

      return { feedback: accumulatedFeedback, haltedForConflict: false };
    },
    [],
  );

  const processImportFiles = useCallback(
    async (files: File[]) => {
      // Preserve accumulated feedback from previous batch if continuing after conflict
      const existingFeedback = importQueueRef.current?.feedback ?? [];
      const feedback: ImportFeedbackItem[] = [...existingFeedback];

      // Initialize queue ref for potential conflict continuation
      importQueueRef.current = { remainingFiles: files, feedback };

      for (const file of files) {
        const { haltedForConflict } = await processSingleImportFile(file, feedback);
        if (haltedForConflict) {
          // Update queue with remaining files
          const idx = files.indexOf(file);
          importQueueRef.current = {
            remainingFiles: files.slice(idx + 1),
            feedback: [...feedback],
          };
          return;
        }
      }

      // All files processed
      importQueueRef.current = null;
      setImportFeedback(feedback);
      setShowImportFeedback(true);
      await loadTemplates();
    },
    [processSingleImportFile, loadTemplates],
  );

  // Helper: after resolving a conflict, continue processing remaining files
  const continueImportAfterConflict = useCallback(
    async (extraFeedback: ImportFeedbackItem) => {
      setImportConflictTemplate(null);
      setImportConflictData(null);

      const queue = importQueueRef.current;
      if (queue && queue.remainingFiles.length > 0) {
        queue.feedback.push(extraFeedback);
        // Refresh templates before continuing so next conflict check is accurate
        await loadTemplates();
        await processImportFiles(queue.remainingFiles);
      } else {
        // Issue 2: preserve accumulated feedback even when queue is empty
        const allFeedback = queue ? [...queue.feedback, extraFeedback] : [extraFeedback];
        setImportFeedback(allFeedback);
        setShowImportFeedback(true);
        await loadTemplates();
      }
    },
    [loadTemplates, processImportFiles],
  );

  const handleImportConflictUpdate = useCallback(async () => {
    if (!importConflictTemplate || !importConflictData) return;

    try {
      await loopTemplateService.update(importConflictTemplate.id, {
        name: importConflictData.name,
        description: importConflictData.description,
        recoveryPolicy: importConflictData.recoveryPolicy,
        nodes: importConflictData.nodes,
        edges: importConflictData.edges,
      });

      await continueImportAfterConflict({
        filename: importConflictData.filename,
        status: "success",
        message: `Updated "${importConflictData.name}" successfully.`,
      });
    } catch (err) {
      await continueImportAfterConflict({
        filename: importConflictData.filename,
        status: "error",
        message: loadErrorMessage(err, "Failed to update template."),
      });
    }
  }, [importConflictTemplate, importConflictData, continueImportAfterConflict]);

  const handleImportConflictSkip = useCallback(async () => {
    if (!importConflictData) return;

    await continueImportAfterConflict({
      filename: importConflictData.filename,
      status: "skipped",
      message: `Skipped "${importConflictData.name}" (already exists).`,
    });
  }, [importConflictData, continueImportAfterConflict]);

  const triggerImportFilePicker = () => {
    importFileInputRef.current?.click();
  };

  const handleImportFileChange = (event: React.ChangeEvent<HTMLInputElement>) => {
    const files = event.target.files;
    if (!files || files.length === 0) return;
    void processImportFiles(Array.from(files));
    event.target.value = ""; // reset so same file can be re-selected
  };

  const onInit = useCallback((flow: any) => {
    reactFlowInstance.current = flow;
  }, []);

  // Identifies the loop currently open in the canvas. Used as the ReactFlow
  // `key` so that opening a different loop (or version) remounts the canvas and
  // re-runs the `fitView` prop — otherwise only the first loop opened gets
  // fitted and later loops risk being drawn outside the viewport.
  const canvasKey = isNewTemplate
    ? "new"
    : `${selectedTemplate?.id ?? "none"}:${readOnlyVersion ?? "latest"}`;

  const onDrop = useCallback(
    (event: React.DragEvent) => {
      event.preventDefault();
      event.stopPropagation();

      // Check for JSON file drops first
      const files = event.dataTransfer.files;
      if (files && files.length > 0) {
        const jsonFiles = Array.from(files).filter((f) => f.name.endsWith(".json"));
        if (jsonFiles.length > 0) {
          void processImportFiles(jsonFiles);
          return;
        }
      }

      // Fall back to node palette drop
      const nodeType = event.dataTransfer.getData("application/loop-node-type");
      if (!nodeType || !reactFlowWrapper.current || !reactFlowInstance.current) return;

      const bounds = reactFlowWrapper.current.getBoundingClientRect();
      const position = reactFlowInstance.current.screenToFlowPosition({
        x: event.clientX - bounds.left,
        y: event.clientY - bounds.top,
      });

      // A Condition node ships with a usable default: the TextMatches variant
      // and a pass-through Output, so its two fixed outlets work out of the box.
      const initialConfig =
        nodeType === NodeType.Condition
          ? { variant: CONDITION_DEFAULT_VARIANT, output: CONDITION_DEFAULT_TEMPLATE }
          : undefined;

      const newNode: Node = {
        id: `node-${Date.now()}`,
        type: "loopNode",
        position,
        data: {
          label: nodeType,
          type: nodeType,
          ...(initialConfig ? { config: initialConfig } : {}),
        },
      };

      setNodes((currentNodes) => currentNodes.concat(newNode));
    },
    [setNodes, processImportFiles],
  );

  const onDragOver = useCallback((event: React.DragEvent) => {
    event.preventDefault();
    // Show copy effect for file drops, move effect for node palette drops
    const isFileDrop = event.dataTransfer.types.includes("Files");
    event.dataTransfer.dropEffect = isFileDrop ? "copy" : "move";
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
      const activeProvider = resolveActiveAiProvider(
        aiProviders,
        (config.aiProviderId as string) || "",
      );
      const resolvedAiTools = resolveToolSelection(activeProvider, config.toolAllowlist);
      // Human/PR nodes declare custom edges in config, but seeded and migrated
      // templates wire the edge without that declaration — union the connected
      // edges in so the settings panel matches what the run actually surfaces.
      const resolvedCustomEdges =
        data.type === NodeType.Human || data.type === NodeType.PR
          ? cleanCustomEdgeNames([
              ...readCustomEdges(config),
              ...getConnectedCustomEdgeNames(node.id, edgesRef.current),
            ])
          : readCustomEdges(config);

      setNodeLabel(data.label || "");
      setCmdCommand((config.command as string) || "");
      setAiPrompt((config.prompt as string) || "");
      setAiProvider((config.aiProviderId as string) || "");
      setAiTools(resolvedAiTools);
      setAiMatchRules(readMatchRules(config));
      setCustomEdgeNames(resolvedCustomEdges);
      setAiUseSession((config.useSession as boolean | undefined) ?? false);
      setAiSessionPlaceholder((config.sessionPlaceholder as string) || "");
      setAiForkFromPlaceholder((config.forkFromPlaceholder as string) || "");
      setStartCreateWorktree((config.createWorktree as boolean) ?? true);
      setStartRunInstall((config.runInstall as boolean) ?? false);
      setHumanInputLabel((config.inputLabel as string) || "");
      setHumanPrompt((config.prompt as string) || "");
      setPromptNodePrompt((config.prompt as string) || "");
      setPrDescriptionTemplate((config.prDescriptionTemplate as string) || "");
      setPrCommentTemplate((config.prCommentTemplate as string) || "");
      setConditionVariant((config.variant as string) || CONDITION_DEFAULT_VARIANT);
      setConditionSubject((config.subject as string) || "");
      setConditionPattern((config.pattern as string) || "");
      setConditionTag((config.tag as string) || "");
      setConditionOutput((config.output as string) ?? CONDITION_DEFAULT_TEMPLATE);
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
        aiPrompt: (config.prompt as string) || "",
        aiProvider: (config.aiProviderId as string) || "",
        aiTools: resolvedAiTools,
        aiMatchRules: readMatchRules(config),
        customEdgeNames: resolvedCustomEdges,
        aiUseSession: (config.useSession as boolean | undefined) ?? false,
        aiSessionPlaceholder: (config.sessionPlaceholder as string) || "",
        aiForkFromPlaceholder: (config.forkFromPlaceholder as string) || "",
        startCreateWorktree: (config.createWorktree as boolean) ?? true,
        startRunInstall: (config.runInstall as boolean) ?? false,
        humanInputLabel: (config.inputLabel as string) || "",
        humanPrompt: (config.prompt as string) || "",
        promptNodePrompt: (config.prompt as string) || "",
        prDescriptionTemplate: (config.prDescriptionTemplate as string) || "",
        prCommentTemplate: (config.prCommentTemplate as string) || "",
        conditionVariant: (config.variant as string) || CONDITION_DEFAULT_VARIANT,
        conditionSubject: (config.subject as string) || "",
        conditionPattern: (config.pattern as string) || "",
        conditionTag: (config.tag as string) || "",
        conditionOutput: (config.output as string) ?? CONDITION_DEFAULT_TEMPLATE,
        adapterConfigValues: initialAdapterValues,
      });
      setShowNodeSettingsModal(true);
    },
    [aiProviders, loadAdapterSchema],
  );

  const handleAiProviderChange = useCallback(
    (providerId: string) => {
      setAiProvider(providerId);
      setAiTools(resolveToolSelection(resolveActiveAiProvider(aiProviders, providerId), []));
      void loadAdapterSchema(providerId);
    },
    [aiProviders, loadAdapterSchema],
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
    } else if (selectedNodeType === NodeType.AI) {
      config.prompt = aiPrompt;
      config.useSession = aiUseSession;
      config.aiProviderId = aiProvider;
      config.toolAllowlist = aiTools;
      config.adapterConfig = { ...adapterConfigValues };
      const cleanRules = aiMatchRules
        .map((rule) => ({ pattern: rule.pattern.trim(), edgeName: rule.edgeName.trim() }))
        .filter((rule) => rule.pattern !== "" && rule.edgeName !== "");
      config.matchRules = cleanRules;
      config.sessionPlaceholder = aiUseSession ? aiSessionPlaceholder.trim() : undefined;
      // A fork-from source is only meaningful with a managed session; an empty
      // value clears it so the node grows its session in place.
      const trimmedForkFrom = aiForkFromPlaceholder.trim();
      config.forkFromPlaceholder = aiUseSession && trimmedForkFrom ? trimmedForkFrom : undefined;
    } else if (selectedNodeType === NodeType.Start) {
      config.createWorktree = startCreateWorktree;
      config.runInstall = startRunInstall;
    } else if (selectedNodeType === NodeType.Human) {
      config.inputLabel = humanInputLabel;
      if (humanPrompt) config.prompt = humanPrompt;
      config.customEdges = cleanCustomEdgeNames(customEdgeNames);
    } else if (selectedNodeType === NodeType.Prompt) {
      if (promptNodePrompt) config.prompt = promptNodePrompt;
    } else if (selectedNodeType === NodeType.PR) {
      if (prDescriptionTemplate) config.prDescriptionTemplate = prDescriptionTemplate;
      if (prCommentTemplate) config.prCommentTemplate = prCommentTemplate;
      config.customEdges = cleanCustomEdgeNames(customEdgeNames);
    } else if (selectedNodeType === NodeType.Condition) {
      const variant = conditionVariant.trim() || CONDITION_DEFAULT_VARIANT;
      config.variant = variant;
      config.output = conditionOutput.trim() || CONDITION_DEFAULT_TEMPLATE;
      // Persist only the params the chosen variant uses; clear the others so a
      // variant switch never leaves stale Subject/Pattern/Tag behind.
      config.subject =
        variant === "TextMatches"
          ? conditionSubject.trim() || CONDITION_DEFAULT_TEMPLATE
          : undefined;
      config.pattern = variant === "TextMatches" ? conditionPattern : undefined;
      config.tag = variant === "HasTag" ? conditionTag.trim() : undefined;
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
    aiMatchRules,
    customEdgeNames,
    aiSessionPlaceholder,
    aiForkFromPlaceholder,
    aiTools,
    aiUseSession,
    adapterConfigValues,
    cmdCommand,
    humanInputLabel,
    humanPrompt,
    nodeLabel,
    prDescriptionTemplate,
    prCommentTemplate,
    promptNodePrompt,
    conditionVariant,
    conditionSubject,
    conditionPattern,
    conditionTag,
    conditionOutput,
    setNodes,
    startCreateWorktree,
    startRunInstall,
  ]);

  const handleCancelNodeSettings = useCallback(() => {
    if (originalNodeConfig) {
      setNodeLabel(originalNodeConfig.label);
      setCmdCommand(originalNodeConfig.cmdCommand);
      setAiPrompt(originalNodeConfig.aiPrompt);
      setAiProvider(originalNodeConfig.aiProvider);
      setAiTools(originalNodeConfig.aiTools);
      setAiMatchRules(originalNodeConfig.aiMatchRules);
      setCustomEdgeNames(originalNodeConfig.customEdgeNames);
      setAiUseSession(originalNodeConfig.aiUseSession);
      setAiSessionPlaceholder(originalNodeConfig.aiSessionPlaceholder);
      setAiForkFromPlaceholder(originalNodeConfig.aiForkFromPlaceholder);
      setStartCreateWorktree(originalNodeConfig.startCreateWorktree);
      setStartRunInstall(originalNodeConfig.startRunInstall);
      setHumanInputLabel(originalNodeConfig.humanInputLabel);
      setHumanPrompt(originalNodeConfig.humanPrompt);
      setPromptNodePrompt(originalNodeConfig.promptNodePrompt);
      setPrDescriptionTemplate(originalNodeConfig.prDescriptionTemplate);
      setPrCommentTemplate(originalNodeConfig.prCommentTemplate);
      setConditionVariant(originalNodeConfig.conditionVariant);
      setConditionSubject(originalNodeConfig.conditionSubject);
      setConditionPattern(originalNodeConfig.conditionPattern);
      setConditionTag(originalNodeConfig.conditionTag);
      setConditionOutput(originalNodeConfig.conditionOutput);
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

      // Condition nodes expose two fixed outlets ("true"/"false"). They wire
      // straight to a Custom edge whose name is the handle id — no name picker,
      // no renaming — so the user cannot add or rename outlets.
      if (connection.sourceHandle === "true" || connection.sourceHandle === "false") {
        const fixedName = connection.sourceHandle;
        const result = checkEdgeConstraints(
          connection.source,
          sourceNode.data?.type as NodeType,
          EdgeType.Custom,
          edges,
        );
        if (!result.allowed) {
          setEdgeError(result.error ?? "Cannot create edge");
          return;
        }
        if (
          edges.some(
            (edge) =>
              edge.source === connection.source &&
              edge.data?.edgeType === EdgeType.Custom &&
              ((edge.data as { name?: string | null })?.name ?? "") === fixedName,
          )
        ) {
          setEdgeError(`The '${fixedName}' edge is already connected from this node`);
          return;
        }
        const newEdge = buildEdge({
          source: connection.source,
          target: connection.target,
          edgeType: EdgeType.Custom,
          name: fixedName,
          maxTraversals: null,
          sourceHandle: fixedName,
          targetHandle: connection.targetHandle ?? "target-handle",
        });
        setEdges((currentEdges) => appendEdge(newEdge, currentEdges));
        setEdgeError(null);
        return;
      }

      let nextEdgeType = EdgeType.OnSuccess;
      if (connection.sourceHandle === "fail") nextEdgeType = EdgeType.OnFailure;
      // The top handle is the single custom outlet; the edge name is chosen in
      // the Configure-Edge panel's "Which edge?" dropdown.
      if (connection.sourceHandle === "respond") nextEdgeType = EdgeType.Custom;

      const result = checkEdgeConstraints(
        connection.source,
        sourceNode.data?.type as NodeType,
        nextEdgeType,
        edges,
      );
      if (!result.allowed) {
        setEdgeError(result.error ?? "Cannot create edge");
        return;
      }

      setEdgeType(nextEdgeType);
      setEdgeName("");
      setEdgeMaxTraversals("");
      setEdgeError(null);
      setPendingConnection(connection);
    },
    [edges, nodes, setEdges],
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

    const trimmedName = edgeName.trim();
    if (edgeType === EdgeType.Custom) {
      if (!trimmedName) {
        setEdgeError("Select which custom edge to connect");
        return;
      }
      if (
        edges.some(
          (edge) =>
            edge.source === pendingConnection.source &&
            edge.data?.edgeType === EdgeType.Custom &&
            ((edge.data as { name?: string | null })?.name ?? "") === trimmedName,
        )
      ) {
        setEdgeError(`The '${trimmedName}' edge is already connected from this node`);
        return;
      }
    }

    const newEdge = buildEdge({
      source: pendingConnection.source,
      target: pendingConnection.target,
      edgeType,
      name: edgeType === EdgeType.Custom ? trimmedName : null,
      maxTraversals: edgeMaxTraversals !== "" ? Number(edgeMaxTraversals) : null,
      sourceHandle: pendingConnection.sourceHandle ?? "success",
      targetHandle: pendingConnection.targetHandle ?? "target-handle",
    });

    setEdges((currentEdges) => appendEdge(newEdge, currentEdges));
    setPendingConnection(null);
    setEdgeName("");
    setEdgeMaxTraversals("");
    setEdgeError(null);
  }, [edgeMaxTraversals, edgeName, edgeType, edges, pendingConnection, setEdges]);

  const cancelEdge = useCallback(() => {
    setPendingConnection(null);
    setEdgeName("");
    setEdgeMaxTraversals("");
    setEdgeError(null);
    setSelectedEdge(null);
    setShowEdgeDeletePanel(false);
  }, []);

  const selectEdge = useCallback(
    (edge: Edge) => {
      setSelectedEdge(edge);
      setSelectedNode(null);
      setShowNodeSettingsModal(false);
      setShowEdgeDeletePanel(true);
      // Mirror React Flow's own path-click selection so a label-selected edge is
      // highlighted too, single-selecting just this one.
      setEdges((currentEdges) =>
        currentEdges.map((candidate) => {
          const shouldSelect = candidate.id === edge.id;
          return candidate.selected === shouldSelect
            ? candidate
            : { ...candidate, selected: shouldSelect };
        }),
      );
    },
    [setEdges],
  );

  const onEdgeClick = useCallback(
    (_event: React.MouseEvent, edge: Edge) => selectEdge(edge),
    [selectEdge],
  );

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
  const showSidebar = sidebarVisible || isNarrow;

  return (
    <div className="page-container">
      <ErrorBanner message={errorText} onDismiss={() => setErrorText("")} />

      {/* Hidden file input for import */}
      <input
        ref={importFileInputRef}
        type="file"
        accept=".json,application/json"
        multiple
        style={{ display: "none" }}
        onChange={handleImportFileChange}
      />

      {/* Import feedback panel */}
      {showImportFeedback && importFeedback.length > 0 && (
        <div className="import-feedback-panel">
          <div className="import-feedback-header">
            <span>Import Results</span>
            <button className="import-feedback-close" onClick={() => setShowImportFeedback(false)}>
              ✕
            </button>
          </div>
          <div className="import-feedback-list">
            {importFeedback.map((item, index) => (
              <div key={index} className={`import-feedback-item import-feedback-${item.status}`}>
                <span className="import-feedback-status-icon">
                  {item.status === "success" ? "✓" : item.status === "error" ? "✗" : "−"}
                </span>
                <span className="import-feedback-filename">{item.filename}</span>
                <span className="import-feedback-message">{item.message}</span>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Import conflict dialog */}
      {importConflictTemplate && importConflictData && (
        <div className="modal-overlay" onMouseDown={handleImportConflictSkip}>
          <div
            className="modal-content import-conflict-modal"
            onMouseDown={(e) => e.stopPropagation()}
            role="dialog"
            aria-modal="true"
            aria-label="Import conflict"
          >
            <div className="modal-header">
              <h2>Template Already Exists</h2>
            </div>
            <div className="modal-body">
              <p>
                A template named <strong>"{importConflictData.name}"</strong> already exists.
              </p>
              <p>What would you like to do?</p>
            </div>
            <div className="modal-footer">
              <button
                type="button"
                className="btn btn-secondary"
                onClick={handleImportConflictSkip}
              >
                Skip
              </button>
              <button
                type="button"
                className="btn btn-primary"
                onClick={handleImportConflictUpdate}
              >
                Update
              </button>
            </div>
          </div>
        </div>
      )}

      <LoopEditorHeader readOnlyVersion={readOnlyVersion} onExitReadOnlyMode={exitReadOnlyMode} />

      <div
        className={`loop-editor-layout ${showSidebar ? "loop-editor-layout-with-sidebar" : "loop-editor-layout-collapsed"}`}
      >
        {showSidebar ? (
          <LoopEditorSidebar
            isNarrow={isNarrow}
            saveSuccess={saveSuccess}
            canSave={Boolean(selectedTemplate || isNewTemplate)}
            isSaving={isSaving}
            isNewTemplate={isNewTemplate}
            newTemplateName={newTemplateName}
            readOnlyVersion={readOnlyVersion}
            showArchived={showArchived}
            templates={templates}
            selectedTemplateId={selectedTemplate?.id ?? null}
            cloningTemplateId={cloningTemplateId}
            cloneName={cloneName}
            showVersionHistory={showVersionHistory}
            versionHistory={versionHistory}
            onToggleSidebar={() => setSidebarVisible(false)}
            onNewTemplateNameChange={setNewTemplateName}
            onSave={handleSave}
            onExport={handleExport}
            onCreateTemplate={handleNewTemplate}
            onImport={triggerImportFilePicker}
            onShowArchivedChange={setShowArchived}
            onSelectTemplate={(template) => navigate(`/loop-editor/${template.id}`)}
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
        ) : (
          <button
            className="loop-editor-sidebar-rail"
            onClick={() => setSidebarVisible(true)}
            aria-label="Expand loop menu"
          >
            <span className="loop-editor-sidebar-rail-grip" aria-hidden="true">
              <span />
              <span />
              <span />
            </span>
            <span className="loop-editor-sidebar-rail-arrow" aria-hidden="true">
              ▶
            </span>
            <span className="loop-editor-sidebar-rail-label" aria-hidden="true">
              LOOPS
            </span>
          </button>
        )}

        <div className="loop-editor-workspace">
          <div className={`node-palette ${readOnlyVersion !== null ? "palette-disabled" : ""}`}>
            <div className="palette-header">Drag &amp; Drop</div>
            {paletteItems.map((item) => (
              <div
                key={item.type}
                className="palette-item"
                draggable={readOnlyVersion === null}
                onDragStart={(event) => onPaletteDragStart(item.type, event)}
              >
                {item.label}
              </div>
            ))}
          </div>

          <div
            className="loop-canvas-container"
            ref={reactFlowWrapper}
            onDrop={onDrop}
            onDragOver={onDragOver}
          >
            {selectedTemplate || isNewTemplate ? (
              <LoopEdgeInteractionContext.Provider value={selectEdge}>
                <ReactFlow
                  key={canvasKey}
                  nodes={nodes}
                  edges={edges}
                  onNodesChange={onNodesChange}
                  onEdgesChange={onEdgesChangeCustom}
                  nodeTypes={nodeTypes}
                  edgeTypes={edgeTypes}
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
                      aiPrompt={aiPrompt}
                      aiProvider={aiProvider}
                      aiTools={aiTools}
                      aiMatchRules={aiMatchRules}
                      customEdgeNames={customEdgeNames}
                      aiUseSession={aiUseSession}
                      aiSessionPlaceholder={aiSessionPlaceholder}
                      aiForkFromPlaceholder={aiForkFromPlaceholder}
                      startCreateWorktree={startCreateWorktree}
                      startRunInstall={startRunInstall}
                      humanInputLabel={humanInputLabel}
                      humanPrompt={humanPrompt}
                      promptNodePrompt={promptNodePrompt}
                      prDescriptionTemplate={prDescriptionTemplate}
                      prCommentTemplate={prCommentTemplate}
                      conditionVariant={conditionVariant}
                      conditionSubject={conditionSubject}
                      conditionPattern={conditionPattern}
                      conditionTag={conditionTag}
                      conditionOutput={conditionOutput}
                      aiProviders={aiProviders}
                      availableAiTools={availableAiTools}
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
                      onAiPromptChange={setAiPrompt}
                      onAiProviderChange={handleAiProviderChange}
                      onAiToolsChange={setAiTools}
                      onAiMatchRulesChange={setAiMatchRules}
                      onCustomEdgeNamesChange={setCustomEdgeNames}
                      onAiUseSessionChange={setAiUseSession}
                      onAiSessionPlaceholderChange={setAiSessionPlaceholder}
                      onAiForkFromPlaceholderChange={setAiForkFromPlaceholder}
                      onStartCreateWorktreeChange={setStartCreateWorktree}
                      onStartRunInstallChange={setStartRunInstall}
                      onHumanInputLabelChange={setHumanInputLabel}
                      onHumanPromptChange={setHumanPrompt}
                      onPromptNodePromptChange={setPromptNodePrompt}
                      onPrDescriptionTemplateChange={setPrDescriptionTemplate}
                      onPrCommentTemplateChange={setPrCommentTemplate}
                      onConditionVariantChange={setConditionVariant}
                      onConditionSubjectChange={setConditionSubject}
                      onConditionPatternChange={setConditionPattern}
                      onConditionTagChange={setConditionTag}
                      onConditionOutputChange={setConditionOutput}
                      onAdapterConfigChange={(name, value) =>
                        setAdapterConfigValues((current) => ({ ...current, [name]: value }))
                      }
                    />
                  )}

                  <EdgePanels
                    pendingConnection={pendingConnection !== null}
                    edgeType={edgeType}
                    edgeName={edgeName}
                    customEdgeOptions={getCustomEdgeNames(
                      nodes.find((node) => node.id === pendingConnection?.source),
                    )}
                    edgeMaxTraversals={edgeMaxTraversals}
                    edgeError={edgeError}
                    showEdgeDeletePanel={showEdgeDeletePanel}
                    selectedEdge={selectedEdge}
                    nodes={nodes}
                    onEdgeNameChange={setEdgeName}
                    onEdgeMaxTraversalsChange={setEdgeMaxTraversals}
                    onConfirmEdge={confirmEdge}
                    onCancelEdge={cancelEdge}
                    onDeleteEdge={deleteSelectedEdge}
                  />
                </ReactFlow>
              </LoopEdgeInteractionContext.Provider>
            ) : (
              <div className="loop-canvas-empty">Select a template to view its graph</div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
