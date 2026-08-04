import { useEffect, useState } from "react";
import {
  WorkItem,
  WorkItemStatus,
  WorkItemPriority,
  AiProviderOverrideMode,
  BranchNameCheck,
} from "../../types";
import { workItemService } from "../../services/auth";
import { parseTags } from "../../utils/workItemJson";
import TagAutocomplete from "../TagAutocomplete";
import type { WorkItemDetail } from "./useWorkItemDetail";

interface EditPanelProps {
  /** The item being edited, or null to create a new one. */
  workItem: WorkItem | null;
  detail: WorkItemDetail;
  onSave: (workItem: WorkItem) => void;
  onDone: () => void;
  /** Reports whether any field differs from its initial value, so the dialog can
   *  guard close paths against discarding unsaved edits. */
  onDirtyChange?: (dirty: boolean) => void;
  /** Opens the dialog's delete confirmation — the delete control lives here in
   *  the edit view rather than the detail footer. Omitted in create mode, where
   *  there is no item to delete. */
  onRequestDelete?: () => void;
}

/**
 * Work item form for the V2 dialog — the same fields and save behaviour for
 * both editing an existing item and creating a new one (when workItem is null),
 * rendered inside the full-screen layout.
 */
export default function EditPanel({
  workItem,
  detail,
  onSave,
  onDone,
  onDirtyChange,
  onRequestDelete,
}: EditPanelProps) {
  // Baselines double as the create-form defaults and the dirty-check reference.
  const baseTitle = workItem?.title ?? "";
  const baseDescription = workItem?.description ?? "";
  const baseStatus = workItem?.status ?? WorkItemStatus.Backlog;
  const basePriority = workItem?.priority ?? WorkItemPriority.Medium;
  const baseTags = workItem ? parseTags(workItem).join(", ") : "";
  const baseRepositoryId = workItem?.repositoryId ?? "";
  const baseAiProviderOverride = workItem?.aiProviderOverride ?? AiProviderOverrideMode.None;
  const baseAiProviderOverrideId = workItem?.aiProviderOverrideId ?? "";
  const baseBranchNameOverride = workItem?.branchNameOverride ?? "";
  // "base" here is the baseline-value prefix every field above uses, applied to
  // the field named baseBranchOverride — not a doubling of the branch's base.
  const baseBaseBranchOverride = workItem?.baseBranchOverride ?? "";

  const [title, setTitle] = useState(baseTitle);
  const [description, setDescription] = useState(baseDescription);
  const [status, setStatus] = useState<WorkItemStatus>(baseStatus);
  const [priority, setPriority] = useState<WorkItemPriority>(basePriority);
  const [tags, setTags] = useState(baseTags);
  const [repositoryId, setRepositoryId] = useState(baseRepositoryId);
  const [aiProviderOverride, setAiProviderOverride] =
    useState<AiProviderOverrideMode>(baseAiProviderOverride);
  const [aiProviderOverrideId, setAiProviderOverrideId] = useState(baseAiProviderOverrideId);
  const [branchNameOverride, setBranchNameOverride] = useState(baseBranchNameOverride);
  const [branchNameCheck, setBranchNameCheck] = useState<BranchNameCheck | null>(null);
  const [baseBranchOverride, setBaseBranchOverride] = useState(baseBaseBranchOverride);
  const [submitting, setSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);

  const overridesProvider = aiProviderOverride !== AiProviderOverrideMode.None;

  // Advice on the branch name, debounced while typing. Deliberately never gates
  // the submit button: a warning means the name is taken *right now*, and the
  // binding check is the one the engine takes when the run starts.
  useEffect(() => {
    const name = branchNameOverride.trim();
    if (!name) {
      setBranchNameCheck(null);
      return;
    }
    let cancelled = false;
    const timer = setTimeout(() => {
      workItemService
        .checkBranchName(name, { repositoryId, workItemId: workItem?.id })
        .then((result) => {
          if (!cancelled) setBranchNameCheck(result);
        })
        .catch(() => {
          // Advice only — a failed lookup must not surface as a form error.
          if (!cancelled) setBranchNameCheck(null);
        });
    }, 400);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [branchNameOverride, repositoryId, workItem?.id]);

  const branchNameProblem = branchNameCheck?.error ?? branchNameCheck?.warning ?? null;

  const dirty =
    title !== baseTitle ||
    description !== baseDescription ||
    status !== baseStatus ||
    priority !== basePriority ||
    tags !== baseTags ||
    repositoryId !== baseRepositoryId ||
    aiProviderOverride !== baseAiProviderOverride ||
    aiProviderOverrideId !== baseAiProviderOverrideId ||
    branchNameOverride !== baseBranchNameOverride ||
    baseBranchOverride !== baseBaseBranchOverride;

  useEffect(() => {
    onDirtyChange?.(dirty);
  }, [dirty, onDirtyChange]);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (submitting) return;
    setSubmitting(true);
    setSubmitError(null);

    const parsedTags = tags
      .split(",")
      .map((t) => t.trim())
      .filter(Boolean);

    const data = {
      title,
      description,
      status,
      priority,
      tags: parsedTags,
      repositoryId,
      aiProviderOverride,
      // Only carry a target when actually overriding, so switching back to
      // "no override" clears the stored provider.
      aiProviderOverrideId: overridesProvider ? aiProviderOverrideId : "",
      // Empty means "back to the generated per-run name", which the server
      // reads as a deliberate clear rather than "leave it alone".
      branchNameOverride: branchNameOverride.trim(),
      // Same convention: empty is a deliberate "back to the repository's
      // default branch", not "leave it alone".
      baseBranchOverride: baseBranchOverride.trim(),
    };

    try {
      let saved: WorkItem;
      if (workItem) {
        saved = await workItemService.update(workItem.id, data as Partial<WorkItem>);
        if (workItem.status !== status) {
          try {
            await workItemService.transition(workItem.id, status);
            saved = await workItemService.getById(workItem.id);
          } catch (err) {
            console.error("Failed to transition status:", err);
            setSubmitError(
              `Status transition failed: ${err instanceof Error ? err.message : "Unknown error"}`,
            );
          }
        }
      } else {
        saved = await workItemService.create(data as Partial<WorkItem>);
      }
      onSave(saved);
      onDone();
    } catch (error) {
      console.error("Failed to save work item:", error);
      setSubmitError(
        `Failed to save: ${(error as { message?: string })?.message ?? "Unknown error"}`,
      );
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <form onSubmit={handleSubmit} className="wiv2-edit-form">
      <div className="form-group">
        <label htmlFor="wiv2-title">Title</label>
        <input
          id="wiv2-title"
          type="text"
          value={title}
          onChange={(e) => setTitle(e.target.value)}
          required
        />
      </div>
      <div className="form-group">
        <label htmlFor="wiv2-description">Description</label>
        <textarea
          id="wiv2-description"
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          rows={10}
        />
      </div>
      <div className="form-row">
        <div className="form-group">
          <label htmlFor="wiv2-repository">Repository</label>
          <select
            id="wiv2-repository"
            value={repositoryId}
            onChange={(e) => setRepositoryId(e.target.value)}
            required
          >
            <option value="">Select repository...</option>
            {detail.repositories.map((r) => (
              <option key={r.id} value={r.id}>
                {r.name}
              </option>
            ))}
          </select>
        </div>
        <div className="form-group">
          <label htmlFor="wiv2-status">Status</label>
          <select
            id="wiv2-status"
            value={status}
            onChange={(e) => setStatus(e.target.value as WorkItemStatus)}
          >
            <option value={WorkItemStatus.Backlog}>Backlog</option>
            <option value={WorkItemStatus.WorkQueue}>Work Queue</option>
            <option value={WorkItemStatus.Ready}>Ready</option>
            <option value={WorkItemStatus.Running}>Running</option>
            <option value={WorkItemStatus.HumanFeedback}>Human Feedback</option>
            <option value={WorkItemStatus.Done}>Done</option>
          </select>
        </div>
        <div className="form-group">
          <label htmlFor="wiv2-priority">Priority</label>
          <select
            id="wiv2-priority"
            value={priority}
            onChange={(e) => setPriority(e.target.value as WorkItemPriority)}
          >
            <option value={WorkItemPriority.Low}>Low</option>
            <option value={WorkItemPriority.Medium}>Medium</option>
            <option value={WorkItemPriority.High}>High</option>
            <option value={WorkItemPriority.Critical}>Critical</option>
          </select>
        </div>
      </div>
      <div className="form-group">
        <label htmlFor="wiv2-tags">
          Tags (comma separated) — each tag must match a loop template name
        </label>
        <TagAutocomplete
          id="wiv2-tags"
          value={tags}
          onChange={setTags}
          options={detail.templates.map((t) => t.name)}
          placeholder="e.g. build, deploy"
        />
      </div>
      <div className="form-row">
        <div className="form-group">
          <label htmlFor="wiv2-ai-override">AI provider override</label>
          <select
            id="wiv2-ai-override"
            value={aiProviderOverride}
            onChange={(e) => setAiProviderOverride(e.target.value as AiProviderOverrideMode)}
          >
            <option value={AiProviderOverrideMode.None}>Default (no override)</option>
            <option value={AiProviderOverrideMode.OverrideDefault}>
              Override default provider only
            </option>
            <option value={AiProviderOverrideMode.OverrideAll}>Override all providers</option>
          </select>
        </div>
        {overridesProvider && (
          <div className="form-group">
            <label htmlFor="wiv2-ai-override-provider">Provider</label>
            <select
              id="wiv2-ai-override-provider"
              value={aiProviderOverrideId}
              onChange={(e) => setAiProviderOverrideId(e.target.value)}
            >
              <option value="">Select provider...</option>
              {detail.aiProviders.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          </div>
        )}
      </div>
      <div className="form-group">
        <label htmlFor="wiv2-branch-name">Branch name (optional)</label>
        <input
          id="wiv2-branch-name"
          type="text"
          value={branchNameOverride}
          onChange={(e) => setBranchNameOverride(e.target.value)}
          placeholder="Leave empty for a generated branch per run"
          aria-describedby="wiv2-branch-name-hint"
        />
        <small
          id="wiv2-branch-name-hint"
          className={branchNameProblem ? "form-hint is-problem" : "form-hint"}
        >
          {branchNameProblem ??
            "Used verbatim by every run of this item. Only the next run is affected."}
        </small>
      </div>
      <div className="form-group">
        <label htmlFor="wiv2-base-branch">Base branch (optional)</label>
        <input
          id="wiv2-base-branch"
          type="text"
          value={baseBranchOverride}
          onChange={(e) => setBaseBranchOverride(e.target.value)}
          placeholder="Leave empty for the repository's default branch"
          aria-describedby="wiv2-base-branch-hint"
        />
        <small id="wiv2-base-branch-hint" className="form-hint">
          Runs start from this branch and open their PR against it. It must exist on the remote when
          the run starts, or the run fails. Only the next run is affected.
        </small>
      </div>
      {submitError && (
        <div role="alert" className="form-error">
          {submitError}
        </div>
      )}
      <div className="wiv2-edit-actions">
        {workItem && onRequestDelete && (
          <button
            type="button"
            className="btn btn-sm btn-danger"
            onClick={onRequestDelete}
            disabled={submitting}
          >
            Delete
          </button>
        )}
        <span className="wiv2-edit-actions-spacer" />
        <button type="button" className="btn btn-secondary" onClick={onDone} disabled={submitting}>
          Cancel
        </button>
        <button type="submit" className="btn btn-primary" disabled={submitting}>
          {submitting ? "Saving..." : workItem ? "Update" : "Create"}
        </button>
      </div>
    </form>
  );
}
