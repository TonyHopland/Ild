import { useEffect, useRef, useState } from "react";
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
import AttachmentPicker from "./AttachmentPicker";
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
  const baseAttachToExistingBranch = workItem?.attachToExistingBranch ?? false;

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
  // Keyed by what it was asked about: the checkbox below may only follow an
  // answer about the name and repository in the form now, never an older one.
  const [branchNameCheck, setBranchNameCheck] = useState<{
    name: string;
    repositoryId: string;
    result: BranchNameCheck;
  } | null>(null);
  // A tick is a choice about one branch in one repository, so it is held as
  // that target: another name or repository, even one on origin too, shows
  // unticked, and coming back to the ticked one shows it ticked again.
  const [attachedTo, setAttachedTo] = useState<{ name: string; repositoryId: string } | null>(
    baseAttachToExistingBranch
      ? { name: baseBranchNameOverride.trim(), repositoryId: baseRepositoryId }
      : null,
  );
  const attachToExistingBranch =
    attachedTo !== null &&
    attachedTo.name === branchNameOverride.trim() &&
    attachedTo.repositoryId === repositoryId;
  // The check is asked whether the item attaches, but only re-asked when the
  // person ticks or unticks the box: a tick the check itself cleared changes
  // nothing worth asking again about.
  const attachRef = useRef(attachToExistingBranch);
  const [attachAsked, setAttachAsked] = useState(0);
  useEffect(() => {
    attachRef.current = attachToExistingBranch;
  }, [attachToExistingBranch]);
  const [baseBranchOverride, setBaseBranchOverride] = useState(baseBaseBranchOverride);
  const [submitting, setSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);
  // The item a first press already created. A retry after a failed upload saves
  // onto it rather than creating a second work item.
  const [createdId, setCreatedId] = useState<string | null>(null);

  const overridesProvider = aiProviderOverride !== AiProviderOverrideMode.None;
  const attachments = detail.editAttachments;

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
        .checkBranchName(name, {
          repositoryId,
          workItemId: workItem?.id,
          attachToExistingBranch: attachRef.current,
        })
        .then((result) => {
          if (cancelled) return;
          setBranchNameCheck({ name, repositoryId, result });
          // Origin says it has no such branch: there is nothing to attach to.
          // An unanswered check (null) is not that answer, so it clears nothing.
          if (result.existsOnOrigin === false) setAttachedTo(null);
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
  }, [branchNameOverride, repositoryId, workItem?.id, attachAsked]);

  // Only a completed check about the name and repository in the form says
  // anything; still checking, a failed check, or another target shows nothing
  // rather than a guess.
  const currentCheck =
    branchNameCheck?.name === branchNameOverride.trim() &&
    branchNameCheck.repositoryId === repositoryId
      ? branchNameCheck.result
      : null;
  const branchNameProblem = currentCheck?.error ?? currentCheck?.warning ?? null;
  const canAttach = currentCheck?.existsOnOrigin === true;

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
    baseBranchOverride !== baseBaseBranchOverride ||
    attachToExistingBranch !== baseAttachToExistingBranch ||
    attachments.staged.length > 0;

  useEffect(() => {
    onDirtyChange?.(dirty);
  }, [dirty, onDirtyChange]);

  // A tick goes out only when the check has confirmed the branch in the form.
  // Unconfirmed — still checking, failed, or about another name — it goes out
  // as false when the branch or repository changed, since a tick confirmed
  // for the old one says nothing about the new one, and is left off otherwise,
  // so the server keeps the item's own setting rather than a guess.
  const attachPayload = (): boolean | undefined => {
    if (canAttach || !attachToExistingBranch) return attachToExistingBranch;
    const moved =
      branchNameOverride.trim() !== baseBranchNameOverride.trim() ||
      repositoryId !== baseRepositoryId;
    return moved ? false : undefined;
  };

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
      attachToExistingBranch: attachPayload(),
    };

    try {
      // The whole save is one act, request and uploads together: the dialog
      // stays closed to dismissal for all of it, not just the uploading part.
      await detail.whileBusy(async () => {
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
        } else if (createdId) {
          // The create call ignores the status field — only /transition moves an
          // item — so this second save carries the form's values and nothing else.
          saved = await workItemService.update(createdId, data as Partial<WorkItem>);
        } else {
          saved = await workItemService.create(data as Partial<WorkItem>);
          setCreatedId(saved.id);
        }
        onSave(saved);

        // What this save carries is whatever the staging list holds by the time
        // the save returns, never what it held when the button was pressed: the
        // human can drop a file while the request is in flight, and a form that
        // closed on the older answer would discard it without a word. The reread
        // is inside the drain for the same reason — a file staged while it runs
        // is still this save's to send.
        while (attachments.hasPending()) {
          const outcome = await attachments.uploadAll(saved.id);
          // The dialog moved to another work item under this save: what is staged
          // now belongs to that one, and this form is gone.
          if (outcome.abandoned) return;
          if (!outcome.ok) {
            // The item is saved and some files are on it; the form stays open
            // with the stragglers still staged, so pressing Save again sends only
            // those. What already landed is published too — the overview and the
            // board would otherwise hold the copy from before the batch.
            onSave(await workItemService.getById(saved.id).catch(() => saved));
            setSubmitError(outcome.errors.join(" "));
            return;
          }
          // The parent gets the copy that carries the uploads. A reread that fails
          // makes the save no less done, so it keeps the one it already has.
          onSave(await workItemService.getById(saved.id).catch(() => saved));
        }
        onDone();
      });
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
    <form onSubmit={handleSubmit} onPaste={attachments.handlePaste} className="wiv2-edit-form">
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
      <AttachmentPicker staging={attachments} inputId="wiv2-attachments" />
      <div className="form-row">
        <div className="form-group">
          <label htmlFor="wiv2-repository">Repository</label>
          <select
            id="wiv2-repository"
            value={repositoryId}
            onChange={(e) => setRepositoryId(e.target.value)}
            aria-describedby={workItem ? "wiv2-repository-hint" : undefined}
            required
          >
            <option value="">Select repository...</option>
            {detail.repositories.map((r) => (
              <option key={r.id} value={r.id}>
                {r.name}
              </option>
            ))}
          </select>
          {workItem && (
            <small id="wiv2-repository-hint" className="form-hint">
              Only the next run is affected.
            </small>
          )}
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
          onChange={(e) => {
            setBranchNameOverride(e.target.value);
            // No name, no branch to attach to.
            if (!e.target.value.trim()) setAttachedTo(null);
          }}
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
        {canAttach && (
          <>
            <label className="checkbox-label">
              <input
                type="checkbox"
                checked={attachToExistingBranch}
                onChange={(e) => {
                  setAttachedTo(
                    e.target.checked ? { name: branchNameOverride.trim(), repositoryId } : null,
                  );
                  setAttachAsked((n) => n + 1);
                }}
                aria-describedby="wiv2-attach-branch-hint"
              />
              Attach to existing branch
            </label>
            <small id="wiv2-attach-branch-hint" className="form-hint">
              Runs check this branch out as it is on the remote instead of creating it, and never
              rebase it. It may be someone else&apos;s: anything a run pushes goes to that branch,
              and to any pull request open on it.
            </small>
          </>
        )}
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
