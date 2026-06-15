import { useState, useEffect, useCallback, useRef } from "react";
import {
  WorkItem,
  WorkItemStatus,
  Repository,
  LoopTemplate,
  LoopRun,
  WorktreePreview,
} from "../../types";
import type { TypedSignalRMessage } from "../../types/signalr";
import {
  workItemService,
  repositoryService,
  loopTemplateService,
  loopRunService,
} from "../../services/auth";
import { useSignalR } from "../../hooks/useSignalR";

/**
 * Shared data + actions for the V2 work item dialog mockups. Mirrors the
 * behaviour of the classic WorkItemModal so each layout variant stays
 * functionally identical while we compare layouts.
 */
export function useWorkItemDetail(workItem: WorkItem | null, onSave: (wi: WorkItem) => void) {
  const [runs, setRuns] = useState<LoopRun[]>([]);
  const [currentRun, setCurrentRun] = useState<LoopRun | null>(null);
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
  const [pushBranchLoading, setPushBranchLoading] = useState(false);
  const [pushBranchError, setPushBranchError] = useState<string | null>(null);
  const [pushBranchMessage, setPushBranchMessage] = useState<string | null>(null);

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

  // Detail for the work item's current run — its pinned template and the node
  // the engine is on power the loop/current-node line in the overview. The
  // run list endpoint omits both, so the detail is fetched separately. The
  // parent refetches the work item on every node state change, so depending on
  // workItem identity keeps the current node fresh as the run advances.
  useEffect(() => {
    const runId = workItem?.currentLoopRunId;
    if (!runId) {
      setCurrentRun(null);
      return;
    }
    let cancelled = false;
    loopRunService
      .getById(runId)
      .then((r) => {
        if (!cancelled) setCurrentRun(r);
      })
      .catch(() => {
        if (!cancelled) setCurrentRun(null);
      });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [workItem]);

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

  // Commit all changes and push the branch to origin — for keeping work from
  // a loop that has no PR node. Mirrors the preview action shape.
  const handlePushBranch = useCallback(async () => {
    if (!workItem) return;
    setPushBranchLoading(true);
    setPushBranchError(null);
    setPushBranchMessage(null);
    try {
      const result = await workItemService.pushBranch(workItem.id);
      setPushBranchMessage(`Pushed ${result.branch} to origin.`);
    } catch (error) {
      setPushBranchError((error as { message?: string })?.message ?? "Failed to push branch.");
    } finally {
      setPushBranchLoading(false);
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

  // Backlog→live handoff state. `seeded` flips true once the replayed backlog
  // has been written; until then live chunks are queued. `seededSeq` is the
  // last sequence number already reflected in `progressText`, so a chunk is
  // applied only once even if it appears in both the backlog and the live feed.
  const seededRef = useRef(false);
  const seededSeqRef = useRef(0);
  const pendingRef = useRef<{ seq: number; line: string }[]>([]);

  useEffect(() => {
    if (!shouldStream || !workItem?.currentLoopRunId) {
      setProgressText("");
      return;
    }
    // Wait for the connection before subscribing; the effect re-runs (and
    // re-seeds from the buffer) when the state flips to "connected".
    if (runConnectionState !== "connected") return;

    const runId = workItem.currentLoopRunId;
    const delayedTimers: number[] = [];
    let cancelled = false;

    seededRef.current = false;
    seededSeqRef.current = 0;
    pendingRef.current = [];
    setProgressText("");

    const applyChunk = (seq: number, line: string) => {
      if (seq <= seededSeqRef.current) return; // already in the backlog/applied
      seededSeqRef.current = seq;
      setProgressText((prev) => prev + line);
    };

    const flushPending = () => {
      const queued = pendingRef.current.sort((a, b) => a.seq - b.seq);
      pendingRef.current = [];
      for (const c of queued) applyChunk(c.seq, c.line);
    };

    const onNodeProgress = (message: TypedSignalRMessage<"NodeProgress">) => {
      if (message.payload.runId !== runId) return;
      const { seq, line } = message.payload;
      if (!seededRef.current) {
        pendingRef.current.push({ seq, line });
        return;
      }
      applyChunk(seq, line);
    };

    const refetchSoon = () => {
      refetchWorkItem();
      // Delayed refetch to catch conversation data that may not be persisted yet
      delayedTimers.push(setTimeout(refetchWorkItem, 500));
    };

    const onLoopRunStateChanged = (message: TypedSignalRMessage<"LoopRunStateChanged">) => {
      if (message.payload.runId !== runId) return;
      refetchSoon();
    };

    const onNodeStateChanged = (message: TypedSignalRMessage<"NodeStateChanged">) => {
      if (message.payload.runId !== runId) return;
      refetchSoon();
    };

    const onEventLogged = (message: TypedSignalRMessage<"EventLogged">) => {
      if (message.payload.runId !== runId) return;
      refetchWorkItem();
    };

    // A halt parks the run mid-AI-node; refetch so the work item flips to
    // HumanFeedback and the Runs tab swaps the live view for the steer window.
    const onRunHalted = (message: TypedSignalRMessage<"RunHalted">) => {
      if (message.payload.runId !== runId) return;
      refetchSoon();
    };

    // Attach the live listener BEFORE subscribing so no chunk is lost in the
    // window between joining the group and seeding from the backlog.
    runOn("NodeProgress", onNodeProgress);
    runOn("LoopRunStateChanged", onLoopRunStateChanged);
    runOn("NodeStateChanged", onNodeStateChanged);
    runOn("EventLogged", onEventLogged);
    runOn("RunHalted", onRunHalted);

    void Promise.resolve(runInvoke?.("SubscribeToRun", runId))
      .then((snapshot) => {
        if (cancelled) return;
        const snap = (snapshot ?? {}) as { text?: string; lastSeq?: number };
        setProgressText(snap.text ?? "");
        seededSeqRef.current = snap.lastSeq ?? 0;
        seededRef.current = true;
        flushPending();
      })
      .catch(() => {
        // Replay unavailable — fall back to a pure-live view from here on.
        if (cancelled) return;
        seededRef.current = true;
        flushPending();
      });

    return () => {
      cancelled = true;
      runOff("NodeProgress", onNodeProgress);
      runOff("LoopRunStateChanged", onLoopRunStateChanged);
      runOff("NodeStateChanged", onNodeStateChanged);
      runOff("EventLogged", onEventLogged);
      runOff("RunHalted", onRunHalted);
      for (const t of delayedTimers) clearTimeout(t);
    };
  }, [
    shouldStream,
    runOn,
    runOff,
    runInvoke,
    runConnectionState,
    workItem?.currentLoopRunId,
    refetchWorkItem,
  ]);

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

  // Route the parked node to one of its named custom edges (a Human/PR button).
  const handleEdge = (name: string) =>
    runAction((id) => workItemService.humanFeedbackEdge(id, name, feedbackInput || ""), "respond");

  // Halt/steer act on the work item's current run, not the work item itself,
  // but reuse runAction's refetch-and-save so the dialog reflects the new state.
  const handleHalt = () => {
    const runId = workItem?.currentLoopRunId;
    if (!runId) return Promise.resolve();
    return runAction(() => loopRunService.halt(runId), "halt run");
  };

  const handleResumeSteer = (note?: string) => {
    const runId = workItem?.currentLoopRunId;
    if (!runId) return Promise.resolve();
    return runAction(() => loopRunService.resumeSteer(runId, note), "resume run");
  };

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
    currentRun,
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
    pushBranchLoading,
    pushBranchError,
    pushBranchMessage,
    handlePushBranch,
    handleApprove,
    handleReject,
    handleEdge,
    handleHalt,
    handleResumeSteer,
    handleCleanupDone,
    handleCleanupBacklog,
    handleLinkPr,
    handleAddDependency,
    handleRemoveDependency,
  };
}

export type WorkItemDetail = ReturnType<typeof useWorkItemDetail>;
