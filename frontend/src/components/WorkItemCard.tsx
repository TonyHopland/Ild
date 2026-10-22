import { useEffect, useState } from "react";
import { WorkItem, WorkItemStatus } from "../types";
import { makeLoopTagMatcher, parseTags } from "../utils/workItemJson";
import { formatDurationMs } from "../utils/duration";
import { prStatusBadges } from "../utils/prStatusBadges";
import Icon from "./Icon";
import {
  feedbackReasonLabel,
  feedbackReasonTone,
  pillClass,
  workItemStatusLabel,
} from "../utils/labels";

interface WorkItemCardProps {
  workItem: WorkItem;
  onClick?: (workItem: WorkItem) => void;
  onMove?: (workItem: WorkItem, direction: "prev" | "next") => void;
  /** Loop template names, used to mark tags that resolve to a loop. */
  loopTemplateNames?: string[];
  repositoryName?: string;
  /** Marks the edge a card dragged over the column would be dropped at. */
  dropEdge?: "above" | "below";
}

export default function WorkItemCard({
  workItem,
  onClick,
  onMove,
  loopTemplateNames = [],
  repositoryName,
  dropEdge,
}: WorkItemCardProps) {
  const isLoopTag = makeLoopTagMatcher(loopTemplateNames);
  const handleDragStart = (e: React.DragEvent) => {
    e.dataTransfer.setData("text/plain", workItem.id);
  };

  const handleClick = () => {
    onClick?.(workItem);
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "ArrowRight") {
      e.preventDefault();
      onMove?.(workItem, "next");
    } else if (e.key === "ArrowLeft") {
      e.preventDefault();
      onMove?.(workItem, "prev");
    } else if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      onClick?.(workItem);
    }
  };

  // Running cards surface the current step and a live-ticking elapsed time so
  // the board gives a fast overview of in-flight work without opening each item.
  const isRunning = workItem.status === WorkItemStatus.Running;
  const startedAtMs = workItem.startedAt ? new Date(workItem.startedAt).getTime() : null;

  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!isRunning || startedAtMs === null) return;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [isRunning, startedAtMs]);

  const elapsed = startedAtMs !== null ? formatDurationMs(now - startedAtMs) : null;
  const showRunningMeta = isRunning && (Boolean(workItem.currentNodeLabel) || elapsed !== null);

  // While parked at a PR node, surface the same CI/review/merge tags the detail
  // dialog's PR view shows so the board gives a fast read on the PR's state.
  const prBadges =
    workItem.status === WorkItemStatus.HumanFeedback &&
    workItem.humanFeedbackReason === "PR Awaiting Merge" &&
    workItem.prStatus
      ? prStatusBadges(workItem.prStatus)
      : [];

  // Those badges are gated on the item being parked at a PR node, so without
  // this a card loses every trace of its PRs the moment the run finishes and it
  // lands in Done. The count, not just a marker: a retried item opens one PR per
  // run (ADR-0008) and the board should say how many there were (WI-203).
  const prCount = workItem.pullRequests?.length ?? 0;
  const proposalCount = workItem.pendingEditProposalCount ?? 0;
  const proposalLabel = proposalCount === 1 ? "1 proposed edit" : `${proposalCount} proposed edits`;

  return (
    <div
      className={`work-item-card${dropEdge ? ` work-item-card--drop-${dropEdge}` : ""}`}
      data-work-item-id={workItem.id}
      draggable
      onDragStart={handleDragStart}
      onClick={handleClick}
      onKeyDown={handleKeyDown}
      role="button"
      tabIndex={0}
      aria-label={`${workItem.title}, status ${workItemStatusLabel(workItem.status)}. Use left and right arrow keys to move between columns.`}
    >
      {workItem.isPreviewRunning && (
        <span className="qa-active-dot" aria-label="QA preview is active" />
      )}
      <div className="work-item-id">
        <span>#{workItem.id}</span>
        {repositoryName && (
          <span className="work-item-repository" title={`Repository: ${repositoryName}`}>
            {repositoryName}
          </span>
        )}
      </div>
      <h4 className="work-item-title">{workItem.title}</h4>
      {/* Why the item waits on a person, in the same words and colours as the
          work item dialog: amber needs input, red failed, blue awaits a merge. */}
      {workItem.humanFeedbackReason && (
        <div className="human-feedback-badge">
          <span
            className={`${pillClass(feedbackReasonTone(workItem.humanFeedbackReason))} pill-dot`}
          >
            {feedbackReasonLabel(workItem.humanFeedbackReason)}
          </span>
        </div>
      )}
      {workItem.status === WorkItemStatus.Ready && workItem.repositoryParallelismHold && (
        <div className="work-item-repository-hold">
          {`Waiting: repository parallelism limit (${workItem.repositoryParallelismHold.inProgress}/${workItem.repositoryParallelismHold.limit} in progress)`}
        </div>
      )}
      {prBadges.length > 0 && (
        <div className="work-item-pr-badges">
          {prBadges.map((badge) => (
            <span key={badge.label} className={`preview-state preview-state--${badge.tone}`}>
              {badge.label}
            </span>
          ))}
        </div>
      )}
      {prCount > 0 && (
        <div className="work-item-pr-history" title="Pull requests opened for this item">
          {prCount === 1 ? "1 PR" : `${prCount} PRs`}
        </div>
      )}
      {proposalCount > 0 && (
        <div className="work-item-edit-proposals" title={proposalLabel}>
          {proposalLabel}
        </div>
      )}
      {showRunningMeta && (
        <div className="work-item-running-meta">
          {workItem.currentNodeLabel && (
            <span className="work-item-step" title="Current step">
              <span className="work-item-step-dot" aria-hidden="true" />
              <span className="work-item-step-label">{workItem.currentNodeLabel}</span>
            </span>
          )}
          {elapsed && (
            <span
              className="work-item-elapsed"
              title="Time elapsed since start"
              aria-label={`Running for ${elapsed}`}
            >
              {elapsed}
            </span>
          )}
        </div>
      )}
      <div className="work-item-tags">
        {parseTags(workItem).map((label) => (
          <span
            key={label}
            className={`work-item-tag${isLoopTag(label) ? " work-item-tag--loop" : ""}`}
            title={isLoopTag(label) ? `Runs the ${label} loop` : undefined}
          >
            {isLoopTag(label) && <Icon name="loop" size={11} />}
            {label}
          </span>
        ))}
      </div>
      <style href="work-item-card" precedence="default">{`
        .work-item-card {
          background-color: var(--surface-2);
          border-radius: var(--radius);
          padding: 10px 12px;
          cursor: grab;
          transition: transform 0.1s ease, box-shadow 0.1s ease, border-color 0.1s ease;
          border: 1px solid var(--border);
          position: relative;
        }

        .work-item-card:hover {
          border-color: var(--border-strong);
        }

        .work-item-card:hover {
          transform: translateY(-1px);
          box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3);
        }

        .work-item-card:active {
          cursor: grabbing;
        }

        .work-item-card--drop-above::before,
        .work-item-card--drop-below::after {
          content: "";
          position: absolute;
          left: 0;
          right: 0;
          height: 2px;
          border-radius: 1px;
          background-color: var(--accent);
          pointer-events: none;
        }

        .work-item-card--drop-above::before {
          top: calc(-0.25rem - 2px);
        }

        .work-item-card--drop-below::after {
          bottom: calc(-0.25rem - 2px);
        }

        .work-item-title {
          font-size: 0.875rem;
          font-weight: 500;
          color: #e0e0e0;
          margin-bottom: 0.25rem;
          line-height: 1.4;
        }

        .work-item-id {
          display: flex;
          align-items: baseline;
          gap: 0.5rem;
          min-width: 0;
          padding-right: 1rem;
          font-size: 0.7rem;
          color: #7f849c;
          margin-bottom: 0.35rem;
          letter-spacing: 0.04em;
        }

        .work-item-repository {
          min-width: 0;
          overflow: hidden;
          text-overflow: ellipsis;
          white-space: nowrap;
          color: #a0a0b0;
        }

        .human-feedback-badge {
          margin-bottom: 0.375rem;
        }

        .work-item-repository-hold {
          font-size: var(--fs-xs);
          color: var(--text-2);
          margin-bottom: 0.375rem;
        }

        .work-item-pr-badges {
          display: flex;
          flex-wrap: wrap;
          gap: 0.25rem;
          margin-bottom: 0.375rem;
        }

        .work-item-pr-history {
          display: inline-block;
          font-size: 0.675rem;
          padding: 0.1rem 0.4rem;
          margin-bottom: 0.375rem;
          border-radius: 0.25rem;
          background-color: #1a2744;
          color: #60a5fa;
        }

        .work-item-edit-proposals {
          display: inline-block;
          font-size: 0.675rem;
          padding: 0.1rem 0.4rem;
          margin-bottom: 0.375rem;
          border-radius: 0.25rem;
          background-color: #2d2a1a;
          color: #fbbf24;
        }

        .work-item-pr-history + .work-item-edit-proposals {
          margin-left: 0.25rem;
        }

        .work-item-running-meta {
          display: flex;
          align-items: center;
          justify-content: space-between;
          gap: 0.5rem;
          margin-bottom: 0.375rem;
        }

        .work-item-step {
          display: inline-flex;
          align-items: center;
          gap: 0.35rem;
          min-width: 0;
          font-size: 0.7rem;
          color: #c0c0d0;
        }

        .work-item-step-dot {
          flex-shrink: 0;
          width: 6px;
          height: 6px;
          border-radius: 50%;
          background-color: #6366f1;
        }

        .work-item-step-label {
          overflow: hidden;
          text-overflow: ellipsis;
          white-space: nowrap;
        }

        .work-item-elapsed {
          flex-shrink: 0;
          font-size: 0.7rem;
          color: #7f849c;
          font-variant-numeric: tabular-nums;
        }

        .work-item-tags {
          display: flex;
          flex-wrap: wrap;
          gap: 0.25rem;
        }

        .work-item-tag {
          display: inline-flex;
          align-items: center;
          gap: 4px;
          font-size: var(--fs-xs);
          padding: 1px 8px;
          background-color: var(--surface-3);
          border-radius: 999px;
          color: var(--text-2);
        }

        /* Tags that name a loop template carry the loop icon and the accent tint,
           so they read as "runs this loop" without a legend. */
        .work-item-tag--loop {
          background-color: var(--accent-soft);
          color: var(--loop-tag-text);
        }

        .qa-active-dot {
          position: absolute;
          top: 0.5rem;
          right: 0.5rem;
          width: 8px;
          height: 8px;
          border-radius: 50%;
          background-color: #22c55e;
          box-shadow: 0 0 4px rgba(34, 197, 94, 0.6);
        }

      `}</style>
    </div>
  );
}
