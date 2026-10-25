import { useCallback, useEffect, useRef, useState, type ChangeEvent, type RefObject } from "react";
import { globalVariableService, loopTemplateService } from "../../services/auth";
import type {
  ExportedGlobalVariable,
  GlobalVariable,
  LoopNode,
  LoopNodeEdge,
  LoopTemplate,
  LoopTemplateExport,
  RecoveryPolicy,
} from "../../types";
import {
  downloadExport,
  exportEdgesToLoopNodeEdges,
  exportNodesToLoopNodes,
  parseImportFile,
  pickGlobalVariables,
  usedGlobalVariableNames,
} from "../../utils/loopTemplateExport";
import type { ImportFeedbackItem } from "./types";
import { loadErrorMessage } from "./utils/loadErrorMessage";

interface ImportConflictData {
  filename: string;
  name: string;
  description: string;
  recoveryPolicy: RecoveryPolicy;
  nodes: LoopNode[];
  edges: LoopNodeEdge[];
  globalVariables: ExportedGlobalVariable[];
}

interface LoopImportExportOptions {
  /** The open loop as an export document. Read when an export is asked for. */
  buildExportData: () => LoopTemplateExport;
  /** Reloads the template list, and resolves to it as it is now. */
  loadTemplates: () => Promise<LoopTemplate[]>;
  /** The template list as the editor last read it. */
  templatesRef: RefObject<LoopTemplate[]>;
  setErrorText: (text: string) => void;
}

/**
 * Exporting the open loop to a file, and importing loop files into the
 * template list, both able to carry the global variables a loop uses. Owns the
 * dialogs' state: one import runs at a time, and every question it asks (a
 * template that exists, a variable whose value differs) is answered before the
 * import goes on.
 */
export function useLoopImportExport({
  buildExportData,
  loadTemplates,
  templatesRef,
  setErrorText,
}: LoopImportExportOptions) {
  const [importFeedback, setImportFeedback] = useState<ImportFeedbackItem[]>([]);
  const [showImportFeedback, setShowImportFeedback] = useState(false);
  const [importConflictTemplate, setImportConflictTemplate] = useState<LoopTemplate | null>(null);
  const [importConflictData, setImportConflictData] = useState<ImportConflictData | null>(null);
  const [variableConflict, setVariableConflict] = useState<{
    name: string;
    current: string;
    imported: string;
  } | null>(null);
  const [exportChoice, setExportChoice] = useState<{
    data: LoopTemplateExport;
    variables: ExportedGlobalVariable[];
  } | null>(null);

  // Refs for import queue — avoids window pollution and stale closures
  const importQueueRef = useRef<{
    remainingFiles: File[];
    feedback: ImportFeedbackItem[];
  } | null>(null);
  const variableConflictResolverRef = useRef<((answer: "overwrite" | "keep") => void) | null>(null);
  const importFileInputRef = useRef<HTMLInputElement>(null);
  const mountedRef = useRef(true);

  const handleExport = useCallback(async () => {
    const exportData = buildExportData();
    const used = usedGlobalVariableNames(exportData.nodes);
    if (used.length === 0) {
      downloadExport(exportData, exportData.name);
      return;
    }

    let variables: ExportedGlobalVariable[];
    try {
      variables = pickGlobalVariables(used, await globalVariableService.getAll());
    } catch (err) {
      setErrorText(loadErrorMessage(err, "Failed to load global variables."));
      return;
    }

    if (variables.length === 0) {
      downloadExport(exportData, exportData.name);
      return;
    }
    setExportChoice({ data: exportData, variables });
  }, [buildExportData, setErrorText]);

  const finishExport = useCallback(
    (includeVariables: boolean) => {
      if (!exportChoice) return;
      const { data, variables } = exportChoice;
      setExportChoice(null);
      downloadExport(includeVariables ? { ...data, globalVariables: variables } : data, data.name);
    },
    [exportChoice],
  );

  const cancelExport = useCallback(() => setExportChoice(null), []);

  // --- Import ---
  // The import reads templatesRef rather than state, so a queued file is checked
  // against the list as refreshed after the previous file.

  // Leaving the editor abandons a running import: every step checks mountedRef
  // before it writes, and a variable question still open (or asked later) is
  // answered "keep" so the import waiting on it ends instead of hanging.
  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      variableConflictResolverRef.current?.("keep");
    };
  }, []);

  const askVariableConflict = useCallback(
    (name: string, current: string, imported: string) =>
      new Promise<"overwrite" | "keep">((resolve) => {
        if (!mountedRef.current) {
          resolve("keep");
          return;
        }
        variableConflictResolverRef.current = resolve;
        setVariableConflict({ name, current, imported });
      }),
    [],
  );

  const answerVariableConflict = useCallback((answer: "overwrite" | "keep") => {
    const resolve = variableConflictResolverRef.current;
    variableConflictResolverRef.current = null;
    setVariableConflict(null);
    resolve?.(answer);
  }, []);

  const importGlobalVariables = useCallback(
    async (
      filename: string,
      variables: ExportedGlobalVariable[],
    ): Promise<ImportFeedbackItem[]> => {
      const lines: ImportFeedbackItem[] = [];
      const report = (status: ImportFeedbackItem["status"], message: string) =>
        lines.push({ filename, status, message });

      let existing: GlobalVariable[];
      try {
        existing = await globalVariableService.getAll();
      } catch (err) {
        report("error", loadErrorMessage(err, "Failed to load global variables."));
        return lines;
      }
      const byName = new Map(existing.map((g) => [g.name.toLowerCase(), g]));

      for (const variable of variables) {
        if (!mountedRef.current) break;
        const current = byName.get(variable.name.toLowerCase());
        try {
          if (!current) {
            await globalVariableService.create({ name: variable.name, value: variable.value });
            report("success", `Created global variable "${variable.name}".`);
          } else if (current.value === variable.value) {
            report("skipped", `Global variable "${current.name}" already has this value.`);
          } else {
            const answer = await askVariableConflict(variable.name, current.value, variable.value);
            if (answer === "keep") {
              report("skipped", `Kept the current value of global variable "${current.name}".`);
            } else {
              await globalVariableService.update(current.id, variable.value);
              report("success", `Overwrote global variable "${current.name}".`);
            }
          }
        } catch (err) {
          report(
            "error",
            `Global variable "${variable.name}": ${loadErrorMessage(err, "Failed to write.")}`,
          );
        }
      }
      return lines;
    },
    [askVariableConflict],
  );

  const processSingleImportFile = useCallback(
    async (
      file: File,
      accumulatedFeedback: ImportFeedbackItem[],
    ): Promise<{
      feedback: ImportFeedbackItem[];
      haltedForConflict: boolean;
    }> => {
      try {
        const { document: raw } = await loopTemplateService.upgradeDocument(await file.text());
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
            filename: file.name,
            name: parsed.name,
            description: parsed.description,
            recoveryPolicy: parsed.recoveryPolicy,
            nodes: loopNodes,
            edges: loopEdges,
            globalVariables: parsed.globalVariables ?? [],
          });
          return { feedback: accumulatedFeedback, haltedForConflict: true };
        }

        if (!mountedRef.current) return { feedback: accumulatedFeedback, haltedForConflict: false };

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
        if (parsed.globalVariables?.length) {
          accumulatedFeedback.push(
            ...(await importGlobalVariables(file.name, parsed.globalVariables)),
          );
        }
      } catch (err) {
        accumulatedFeedback.push({
          filename: file.name,
          status: "error",
          message: loadErrorMessage(err, "Failed to import."),
        });
      }

      return { feedback: accumulatedFeedback, haltedForConflict: false };
    },
    [importGlobalVariables],
  );

  const processImportFiles = useCallback(
    async (files: File[]) => {
      // Preserve accumulated feedback from previous batch if continuing after conflict
      const existingFeedback = importQueueRef.current?.feedback ?? [];
      const feedback: ImportFeedbackItem[] = [...existingFeedback];

      // Initialize queue ref for potential conflict continuation
      importQueueRef.current = { remainingFiles: files, feedback };

      for (const file of files) {
        if (!mountedRef.current) return;
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

  // A running import owns the queue and the dialogs; files picked meanwhile are ignored.
  const startImport = useCallback(
    (files: File[]) => {
      if (importQueueRef.current) return;
      void processImportFiles(files);
    },
    [processImportFiles],
  );

  // Helper: after resolving a conflict, continue processing remaining files
  const continueImportAfterConflict = useCallback(
    async (extraFeedback: ImportFeedbackItem[]) => {
      setImportConflictTemplate(null);
      setImportConflictData(null);

      const queue = importQueueRef.current;
      if (queue && queue.remainingFiles.length > 0) {
        queue.feedback.push(...extraFeedback);
        // Refresh templates before continuing so next conflict check is accurate
        await loadTemplates();
        await processImportFiles(queue.remainingFiles);
      } else {
        // Keep the feedback gathered so far even when no files remain
        importQueueRef.current = null;
        const allFeedback = queue ? [...queue.feedback, ...extraFeedback] : extraFeedback;
        setImportFeedback(allFeedback);
        setShowImportFeedback(true);
        await loadTemplates();
      }
    },
    [loadTemplates, processImportFiles],
  );

  const handleImportConflictUpdate = useCallback(async () => {
    if (!importConflictTemplate || !importConflictData) return;
    const { filename, name, description, recoveryPolicy, nodes, edges, globalVariables } =
      importConflictData;

    // The template dialog goes away before any variable is asked about, so it
    // can never be answered again while a variable diff is open.
    setImportConflictTemplate(null);
    setImportConflictData(null);

    let lines: ImportFeedbackItem[];
    try {
      await loopTemplateService.update(importConflictTemplate.id, {
        name,
        description,
        recoveryPolicy,
        nodes,
        edges,
      });
      lines = [
        { filename, status: "success", message: `Updated "${name}" successfully.` },
        ...(await importGlobalVariables(filename, globalVariables)),
      ];
    } catch (err) {
      lines = [
        {
          filename,
          status: "error",
          message: loadErrorMessage(err, "Failed to update template."),
        },
      ];
    }
    await continueImportAfterConflict(lines);
  }, [
    importConflictTemplate,
    importConflictData,
    importGlobalVariables,
    continueImportAfterConflict,
  ]);

  const handleImportConflictSkip = useCallback(async () => {
    if (!importConflictData) return;

    await continueImportAfterConflict([
      {
        filename: importConflictData.filename,
        status: "skipped",
        message: `Skipped "${importConflictData.name}" (already exists).`,
      },
    ]);
  }, [importConflictData, continueImportAfterConflict]);

  const openImportFilePicker = useCallback(() => {
    importFileInputRef.current?.click();
  }, []);

  const handleImportFileChange = useCallback(
    (event: ChangeEvent<HTMLInputElement>) => {
      const files = event.target.files;
      if (!files || files.length === 0) return;
      startImport(Array.from(files));
      event.target.value = ""; // reset so same file can be re-selected
    },
    [startImport],
  );

  const dismissImportFeedback = useCallback(() => setShowImportFeedback(false), []);

  return {
    exportChoice,
    handleExport,
    finishExport,
    cancelExport,
    variableConflict,
    answerVariableConflict,
    importConflictName:
      importConflictTemplate && importConflictData ? importConflictData.name : null,
    handleImportConflictSkip,
    handleImportConflictUpdate,
    importFeedback: showImportFeedback ? importFeedback : [],
    dismissImportFeedback,
    importFileInputRef,
    openImportFilePicker,
    handleImportFileChange,
    startImport,
  };
}
