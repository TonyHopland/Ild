import { useState, useEffect, useCallback } from "react";
import {
  WorkItem,
  WorkItemStatus,
  Repository,
  LoopTemplate,
  LoopRun,
  WorktreePreview,
} from "../../types";
import type { TypedSignalRMessage } from "../../types/signalr";
import { workItemService, repositoryService, loopTemplateService } from "../../services/auth";
import { useSignalR } from "../../hooks/useSignalR";

/**
 * Shared data + actions for the V2 work item dialog mockups. Mirrors the
 * behaviour of the classic WorkItemModal so each layout variant stays
 * functionally identical while we compare layouts.
 */
export function useWorkItemDetail(workItem: WorkItem | null, onSave: (wi: WorkItem) => void) {
  const [runs, setRuns] = useState<LoopRun[]>([]);
  const [dependencies, setDependencies] = useState<WorkItem[]>([]);
  const [allWorkItems, setAllWorkItems] = useState<WorkItem[]>([]);
  const [repositories, setRepositories] = useState<Repository[]>([]);
  const [templates, setTemplates] = useState<LoopTemplate[]>([]);
  const [feedbackInput, setFeedbackInput] = useState("");
  const [prCommentsLoading, setPrCommentsLoading] = useState(false);
  const [progressText, setProgressText] = useState("");
  const [preview, setPreview] = useState<WorktreePreview | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [previewError, setPreviewError] = useState<string | null>(null);

  useEffect(() => {
    repositoryService
      .getAll()
      .then(setRepositories)
      .catch(() => {});
    loopTemplateService
      .getAll()
      .then(setTemplates)
      .catch(() => {});
  }, []);

  const refreshRuns = useCallback(() => {
    if (!workItem) return;
    workItemService
      .getRuns(workItem.id)
      .then((r) => setRuns(Array.isArray(r) ? r : []))
      .catch(() => {});
  }, [workItem?.id]);

  useEffect(() => {
    if (!workItem) return;
    refreshRuns();
    workItemService
      .getDependencies(workItem.id)
      .then((d) => setDependencies(Array.isArray(d) ? d : []))
      .catch(() => {});
    workItemService
      .getAll()
      .then((w) => setAllWorkItems(Array.isArray(w) ? w : []))
      .catch(() => {});
  }, [workItem?.id, refreshRuns]);

  useEffect(() => {
    setFeedbackInput("");
  }, [workItem?.id, workItem?.status]);

  useEffect(() => {
    // When parked at a PR node, prefill the feedback textarea with any
    // unread PR comments so the human can edit them before approving or
    // rejecting. Best-effort: failures leave the textarea empty.
    if (
      !workItem ||
      workItem.status !== WorkItemStatus.HumanFeedback ||
      workItem.humanFeedbackReason !== "PR Awaiting Merge"
    ) {
      return;
    }
    let cancelled = false;
    setPrCommentsLoading(true);
    void (async () => {
      try {
        const comments = await workItemService.getPrComments(workItem.id);
        if (cancelled) return;
        if (Array.isArray(comments) && comments.length > 0) {
          const text = comments
            .map((c) => `${c.author}: ${c.body}`.trim())
            .filter(Boolean)
            .join("\n\n");
          setFeedbackInput((prev) => (prev.length === 0 ? text : prev));
        }
      } catch {
        // Ignore — empty textarea is fine.
      } finally {
        if (!cancelled) setPrCommentsLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [workItem?.id, workItem?.status, workItem?.humanFeedbackReason]);

  const refreshPreview = useCallback(async () => {
    if (!workItem?.id || !workItem.worktreePath) {
      setPreview(null);
      setPreviewError(null);
      return;
    }
    setPreviewLoading(true);
    setPreviewError(null);
    try {
      const result = await workItemService.getPreview(workItem.id);
      setPreview(result);
    } catch (error) {
      const message = (error as { message?: string })?.message ?? "Failed to load preview status.";
      setPreviewError(message);
      setPreview(null);
    } finally {
      setPreviewLoading(false);
    }
  }, [workItem?.id, workItem?.worktreePath]);

  useEffect(() => {
    void refreshPreview();
  }, [refreshPreview]);

  const handleStartPreview = useCallback(async () => {
    if (!workItem) return;
    setPreviewLoading(true);
    setPreviewError(null);
    try {
      const result = await workItemService.startPreview(workItem.id, {});
      setPreview(result);
    } catch (error) {
      setPreviewError((error as { message?: string })?.message ?? "Failed to start preview.");
    } finally {
      setPreviewLoading(false);
    }
  }, [workItem?.id]);

  const handleStopPreview = useCallback(async () => {
    if (!workItem) return;
    setPreviewLoading(true);
    setPreviewError(null);
    try {
      const result = await workItemService.stopPreview(workItem.id);
      setPreview(result);
    } catch (error) {
      setPreviewError((error as { message?: string })?.message ?? "Failed to stop preview.");
    } finally {
      setPreviewLoading(false);
    }
  }, [workItem?.id]);

  const refetchWorkItem = useCallback(() => {
    if (!workItem) return;
    void workItemService
      .getById(workItem.id)
      .then((updated) => onSave(updated))
      .catch(() => {});
  }, [workItem?.id, onSave]);

  // Live stream + state sync for running work items.
  const shouldStream = !!(
    workItem &&
    workItem.status === WorkItemStatus.Running &&
    workItem.currentLoopRunId
  );

  const {
    on: runOn,
    off: runOff,
    invoke: runInvoke,
    connectionState: runConnectionState,
  } = useSignalR("/hubs/loop-run");

  useEffect(() => {
    if (runConnectionState !== "connected" || !shouldStream || !workItem?.currentLoopRunId) return;
    void runInvoke?.("SubscribeToRun", workItem.currentLoopRunId);
  }, [shouldStream, runInvoke, runConnectionState, workItem?.currentLoopRunId]);

  useEffect(() => {
    if (!shouldStream) {
      setProgressText("");
      return;
    }

    const delayedTimers: number[] = [];

    const onNodeProgress = (message: TypedSignalRMessage<"NodeProgress">) => {
      const { runId: msgRunId, line } = message.payload;
      if (msgRunId !== workItem?.currentLoopRunId) return;
      setProgressText((prev) => prev + line);
    };

    const refetchSoon = () => {
      refetchWorkItem();
      // Delayed refetch to catch conversation data that may not be persisted yet
      delayedTimers.push(setTimeout(refetchWorkItem, 500));
    };

    const onLoopRunStateChanged = (message: TypedSignalRMessage<"LoopRunStateChanged">) => {
      if (message.payload.runId !== workItem?.currentLoopRunId) return;
      refetchSoon();
    };

    const onNodeStateChanged = (message: TypedSignalRMessage<"NodeStateChanged">) => {
      if (message.payload.runId !== workItem?.currentLoopRunId) return;
      refetchSoon();
    };

    const onEventLogged = (message: TypedSignalRMessage<"EventLogged">) => {
      if (message.payload.runId !== workItem?.currentLoopRunId) return;
      refetchWorkItem();
    };

    runOn("NodeProgress", onNodeProgress);
    runOn("LoopRunStateChanged", onLoopRunStateChanged);
    runOn("NodeStateChanged", onNodeStateChanged);
    runOn("EventLogged", onEventLogged);

    return () => {
      runOff("NodeProgress", onNodeProgress);
      runOff("LoopRunStateChanged", onLoopRunStateChanged);
      runOff("NodeStateChanged", onNodeStateChanged);
      runOff("EventLogged", onEventLogged);
      for (const t of delayedTimers) clearTimeout(t);
    };
  }, [shouldStream, runOn, runOff, workItem?.currentLoopRunId, refetchWorkItem]);

  // The PR/feedback/cleanup actions all share the same shape: invoke a service
  // call, refetch the work item, and hand it to onSave (logging on failure).
  const runAction = useCallback(
    async (action: (id: string) => Promise<unknown>, errorLabel: string) => {
      if (!workItem) return;
      try {
        await action(workItem.id);
        const updated = await workItemService.getById(workItem.id);
        onSave(updated);
      } catch (error) {
        console.error(`Failed to ${errorLabel}:`, error);
      }
    },
    [workItem?.id, onSave],
  );

  const handleMarkMerged = () => runAction((id) => workItemService.markMerged(id), "mark merged");

  const handleApprove = () =>
    runAction(
      (id) => workItemService.humanFeedbackInput(id, feedbackInput || ""),
      "submit feedback",
    );

  // Pass any typed feedback through to the OnFailure successor as {{PreviousNode.Output}}.
  const handleReject = () =>
    runAction(
      (id) => workItemService.humanFeedbackReject(id, feedbackInput || undefined),
      "reject",
    );

  const handleRespond = () =>
    runAction((id) => workItemService.humanFeedbackRespond(id, feedbackInput || ""), "respond");

  const handleCleanupDone = () =>
    runAction((id) => workItemService.cleanupToDone(id), "cleanup to done");

  const handleCleanupBacklog = () =>
    runAction((id) => workItemService.cleanupToBacklog(id), "cleanup to backlog");

  const handleLinkPr = async (prUrl: string) =>
    runAction((id) => workItemService.linkPr(id, prUrl), "link PR");

  const handleAddDependency = async (depId: string) => {
    if (!workItem || !depId) return;
    try {
      await workItemService.addDependency(workItem.id, depId);
      const deps = await workItemService.getDependencies(workItem.id);
      setDependencies(deps);
    } catch (error) {
      console.error("Failed to add dependency:", error);
    }
  };

  const handleRemoveDependency = async (depId: string) => {
    if (!workItem) return;
    try {
      await workItemService.removeDependency(workItem.id, depId);
      setDependencies((prev) => prev.filter((d) => d.id !== depId));
    } catch (error) {
      console.error("Failed to remove dependency:", error);
    }
  };

  return {
    runs,
    refreshRuns,
    dependencies,
    allWorkItems,
    repositories,
    templates,
    feedbackInput,
    setFeedbackInput,
    prCommentsLoading,
    progressText,
    shouldStream,
    preview,
    previewLoading,
    previewError,
    refreshPreview,
    handleStartPreview,
    handleStopPreview,
    handleMarkMerged,
    handleApprove,
    handleReject,
    handleRespond,
    handleCleanupDone,
    handleCleanupBacklog,
    handleLinkPr,
    handleAddDependency,
    handleRemoveDependency,
  };
}

export type WorkItemDetail = ReturnType<typeof useWorkItemDetail>;
