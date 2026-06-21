import { useEffect, useState } from "react";
import { WorkItem, WorkItemStatus } from "../types";
import { makeLoopTagMatcher, parseTags } from "../utils/workItemJson";
import { formatDurationMs } from "../utils/duration";
import { prStatusBadges } from "../utils/prStatusBadges";

interface WorkItemCardProps {
  workItem: WorkItem;
  onClick?: (workItem: WorkItem) => void;
  onMove?: (workItem: WorkItem, direction: "prev" | "next") => void;
  /** Loop template names, used to mark tags that resolve to a loop. */
  loopTemplateNames?: string[];
}

const REASON_STYLES: Record<string, { bg: string; color: string; border: string }> = {
  "PR Awaiting Merge": { bg: "#1a2744", color: "#60a5fa", border: "#2563eb" },
  "Node Failed": { bg: "#2d1a1a", color: "#f87171", border: "#dc2626" },
  "Rebase Conflict": { bg: "#2d2a1a", color: "#fbbf24", border: "#d97706" },
  "Human Input Needed": { bg: "#1a2d2a", color: "#34d399", border: "#059669" },
};

export default function WorkItemCard({
  workItem,
  onClick,
  onMove,
  loopTemplateNames = [],
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

  const reasonStyle = workItem.humanFeedbackReason
    ? (REASON_STYLES[workItem.humanFeedbackReason] ?? {
        bg: "#2d2d44",
        color: "#a0a0b0",
        border: "#6366f1",
      })
    : null;

  // While parked at a PR node, surface the same CI/review/merge tags the detail
  // dialog's PR view shows so the board gives a fast read on the PR's state.
  const prBadges =
    workItem.status === WorkItemStatus.HumanFeedback &&
    workItem.humanFeedbackReason === "PR Awaiting Merge" &&
    workItem.prStatus
      ? prStatusBadges(workItem.prStatus)
      : [];

  return (
    <div
      className="work-item-card"
      draggable
      onDragStart={handleDragStart}
      onClick={handleClick}
      onKeyDown={handleKeyDown}
      role="button"
      tabIndex={0}
      aria-label={`${workItem.title}, status ${workItem.status}. Use left and right arrow keys to move between columns.`}
    >
      {workItem.isPreviewRunning && (
        <span className="qa-active-dot" aria-label="QA preview is active" />
      )}
      <div className="work-item-id">#{workItem.id}</div>
      <h4 className="work-item-title">{workItem.title}</h4>
      {reasonStyle && workItem.humanFeedbackReason && (
        <div
          className="human-feedback-badge"
          style={{
            backgroundColor: reasonStyle.bg,
            color: reasonStyle.color,
            borderLeft: `3px solid ${reasonStyle.border}`,
          }}
        >
          {workItem.humanFeedbackReason}
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
          >
            {label}
          </span>
        ))}
      </div>
      <style>{`
        .work-item-card {
          background-color: #2a2a40;
          border-radius: 0.375rem;
          padding: 0.75rem;
          cursor: grab;
          transition: transform 0.1s ease, box-shadow 0.1s ease;
          border: 1px solid #3a3a5c;
          position: relative;
        }

        .work-item-card:hover {
          transform: translateY(-1px);
          box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3);
        }

        .work-item-card:active {
          cursor: grabbing;
        }

        .work-item-title {
          font-size: 0.875rem;
          font-weight: 500;
          color: #e0e0e0;
          margin-bottom: 0.25rem;
          line-height: 1.4;
        }

        .work-item-id {
          font-size: 0.7rem;
          color: #7f849c;
          margin-bottom: 0.35rem;
          letter-spacing: 0.04em;
        }

        .human-feedback-badge {
          font-size: 0.7rem;
          font-weight: 600;
          padding: 0.2rem 0.5rem;
          border-radius: 0.25rem;
          margin-bottom: 0.375rem;
          text-transform: uppercase;
          letter-spacing: 0.03em;
        }

        .work-item-pr-badges {
          display: flex;
          flex-wrap: wrap;
          gap: 0.25rem;
          margin-bottom: 0.375rem;
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
          font-size: 0.675rem;
          padding: 0.1rem 0.4rem;
          background-color: #2d2d44;
          border-radius: 0.25rem;
          color: #a0a0b0;
        }

        /* Tags that resolve to a loop template get a purple background so they
           stand out from ordinary free-form labels. */
        .work-item-tag--loop {
          background-color: #4c1d95;
          color: #ddd6fe;
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
