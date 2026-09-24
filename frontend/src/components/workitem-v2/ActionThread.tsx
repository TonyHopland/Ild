import { useEffect, useRef, useState, type ReactNode } from "react";
import { TurnVariableChange, WorkItem, WorkItemStatus } from "../../types";
import { workItemService } from "../../services/auth";
import { parseConversation } from "../../utils/workItemJson";
import MarkdownRenderer from "../MarkdownRenderer";
import LiveStream from "../NodeTimeline/LiveStream";
import HaltSteerControls from "./HaltSteerControls";
import { FeedbackBanner, PrView, QueuedPrWrites } from "./panels";
import { variablesSetByTurn } from "./turnVariables";
import type { WorkItemDetail } from "./useWorkItemDetail";

type Side = "ai" | "human";

function Bubble({
  side,
  author,
  timestamp,
  live,
  footer,
  children,
}: {
  side: Side;
  author?: string;
  timestamp?: string;
  live?: boolean;
  footer?: ReactNode;
  children: ReactNode;
}) {
  return (
    <div className={`wiv2-bubble-row wiv2-bubble-row-${side}`}>
      <div className="wiv2-bubble-meta">
        {author && <strong>{author}</strong>}
        {live && <span className="wiv2-bubble-live">● live</span>}
        {timestamp && <span>{new Date(timestamp).toLocaleString()}</span>}
      </div>
      <div className={`wiv2-bubble wiv2-bubble-${side}`}>
        <div className="wiv2-bubble-body">{children}</div>
        {footer}
      </div>
    </div>
  );
}

function TurnVariables({ variables }: { variables: TurnVariableChange[] }) {
  const [open, setOpen] = useState<ReadonlySet<string>>(new Set());
  const toggle = (name: string) =>
    setOpen((prev) => {
      const next = new Set(prev);
      if (!next.delete(name)) next.add(name);
      return next;
    });
  return (
    <div className="wiv2-turn-vars">
      <div className="wiv2-turn-vars-pills">
        {variables.map((v) => (
          <button
            key={v.name}
            type="button"
            className="wiv2-turn-vars-toggle"
            aria-expanded={open.has(v.name)}
            onClick={() => toggle(v.name)}
          >
            <span className="wiv2-turn-vars-icon">{"{x}"}</span>
            {v.name}
            <span className={`wiv2-turn-var-tag wiv2-turn-var-${v.change}`}>
              {v.change === "created" ? "new" : "changed"}
            </span>
            <span className="wiv2-turn-vars-chevron">{open.has(v.name) ? "▾" : "▸"}</span>
          </button>
        ))}
      </div>
      {variables
        .filter((v) => open.has(v.name))
        .map((v) => (
          <div key={v.name} className="wiv2-turn-var">
            <div className="wiv2-turn-var-head">
              {v.name}
              {v.changedLater && <span className="wiv2-turn-var-later">changed again later</span>}
            </div>
            <pre>{v.value}</pre>
          </div>
        ))}
    </div>
  );
}

/** Whether the current run has a pull request, or anything queued for one, to show. */
function hasPrDetails(workItem: WorkItem, detail: WorkItemDetail): boolean {
  const run = detail.currentRun;
  return !!run?.prSnapshot || (run?.prQueuedWrites?.length ?? 0) > 0 || !!workItem.prUrl;
}

/**
 * The current run's pull request as it stands, rather than a turn in the
 * dialogue: the PR and its review thread, and what the AI has queued to say on
 * it but not sent yet.
 */
function PrDetails({
  workItem,
  detail,
  onOpen,
}: {
  workItem: WorkItem;
  detail: WorkItemDetail;
  onOpen: (panel: HTMLElement) => void;
}) {
  const [open, setOpen] = useState(false);
  const panelRef = useRef<HTMLElement | null>(null);
  useEffect(() => {
    if (open && panelRef.current) onOpen(panelRef.current);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);
  const snapshot = detail.currentRun?.prSnapshot ?? null;
  const pending = detail.currentRun?.prQueuedWrites?.length ?? 0;
  if (!hasPrDetails(workItem, detail)) return null;
  return (
    <section ref={panelRef} className="wiv2-pr-details">
      <div className="wiv2-pr-details-header">
        <button
          type="button"
          className="wiv2-pr-details-toggle"
          aria-expanded={open}
          onClick={() => setOpen((o) => !o)}
        >
          <span className="wiv2-pr-details-chevron">{open ? "▾" : "▸"}</span>
          PR details
          {pending > 0 && <span className="wiv2-pr-details-pending">{pending} pending</span>}
        </button>
        {workItem.prUrl && (
          <a
            className="feedback-pr-link"
            href={workItem.prUrl}
            target="_blank"
            rel="noopener noreferrer"
          >
            Open PR
          </a>
        )}
      </div>
      {open && (
        <div className="wiv2-pr-details-body">
          {snapshot ? (
            <PrView snapshot={snapshot} />
          ) : (
            <div className="wiv2-empty">The pull request has not been fetched yet.</div>
          )}
          <QueuedPrWrites workItem={workItem} detail={detail} />
        </div>
      )}
    </section>
  );
}

/**
 * What each turn of the item did to its variables, re-read whenever the
 * conversation grows — a new turn is the moment a new variable can appear.
 */
function useTurnVariables(workItemId: string, refreshKey: number): TurnVariableChange[] {
  const [changes, setChanges] = useState<TurnVariableChange[]>([]);
  useEffect(() => {
    let cancelled = false;
    void workItemService
      .getTurnVariables(workItemId)
      .catch(() => [])
      .then((c) => {
        if (!cancelled) setChanges(c);
      });
    return () => {
      cancelled = true;
    };
  }, [workItemId, refreshKey]);
  return changes;
}

/**
 * The Action tab as one chronological thread: the conversation so far, the
 * live run as the latest AI bubble, and the pending human feedback as the
 * latest human bubble. Opens scrolled to the newest entry.
 */
export default function ActionThread({
  workItem,
  detail,
  feedbackPrompt,
  active,
}: {
  workItem: WorkItem;
  detail: WorkItemDetail;
  feedbackPrompt: string | null;
  active: boolean;
}) {
  const messages = parseConversation(workItem);
  const turnVariables = useTurnVariables(workItem.id, messages.length);
  const threadRef = useRef<HTMLDivElement | null>(null);
  const pinnedToBottom = useRef(true);
  const awaitingHuman =
    workItem.status === WorkItemStatus.HumanFeedback && !!workItem.humanFeedbackReason;

  // Stay on the newest entry while content loads in and grows beneath it
  // (prompt, PR snapshot, live output), until the reader scrolls away.
  useEffect(() => {
    const thread = threadRef.current;
    const scroller = thread?.closest<HTMLElement>(".wiv2-tabpanel");
    if (!active || !thread || !scroller) return;
    const toBottom = () => {
      if (pinnedToBottom.current) scroller.scrollTop = scroller.scrollHeight;
    };
    const onScroll = () => {
      pinnedToBottom.current =
        scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 40;
    };
    pinnedToBottom.current = true;
    toBottom();
    scroller.addEventListener("scroll", onScroll);
    const ro = typeof ResizeObserver !== "undefined" ? new ResizeObserver(toBottom) : null;
    ro?.observe(thread);
    return () => {
      scroller.removeEventListener("scroll", onScroll);
      ro?.disconnect();
    };
  }, [active]);

  // Opening something tall above the bottom of the thread should show it from
  // its top, not stay pinned to the bottom and push its header out of view.
  const revealFromTop = (el: HTMLElement) => {
    pinnedToBottom.current = false;
    el.scrollIntoView?.({ block: "start" });
  };

  const isEmpty =
    messages.length === 0 &&
    !detail.shouldStream &&
    !awaitingHuman &&
    !hasPrDetails(workItem, detail);

  return (
    <div className="wiv2-thread" ref={threadRef}>
      {messages.map((m, i) => {
        const side: Side = m.role.toLowerCase() === "human" ? "human" : "ai";
        const variables = variablesSetByTurn(m, turnVariables);
        return (
          <Bubble
            key={i}
            side={side}
            author={side === "ai" ? (m.name ?? "AI") : undefined}
            timestamp={m.timestamp}
            footer={variables.length > 0 && <TurnVariables variables={variables} />}
          >
            <div className="conversation-message-content">
              <MarkdownRenderer content={m.content} />
            </div>
          </Bubble>
        );
      })}
      <Bubble side="ai" author="AI" live={detail.shouldStream}>
        {detail.shouldStream && <LiveStream text={detail.progressText} />}
        <HaltSteerControls
          run={detail.currentRun}
          workItemStatus={workItem.status}
          onHalt={detail.handleHalt}
          onResumeSteer={detail.handleResumeSteer}
          onCleanupDone={detail.handleCleanupDone}
          onCleanupBacklog={detail.handleCleanupBacklog}
          showAbandon={false}
        />
      </Bubble>
      <PrDetails workItem={workItem} detail={detail} onOpen={revealFromTop} />
      <Bubble side="human">
        <FeedbackBanner workItem={workItem} detail={detail} prompt={feedbackPrompt} />
      </Bubble>
      {isEmpty && <div className="wiv2-empty">No action required.</div>}
    </div>
  );
}
