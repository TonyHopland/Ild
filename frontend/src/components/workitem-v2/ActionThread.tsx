import { useEffect, useRef, useState, type ReactNode } from "react";
import { LoopRunStatus, TurnVariableChange, WorkItem } from "../../types";
import { useEditProposals } from "../../hooks/useEditProposals";
import { useGlobalVariableProposals } from "../../hooks/useGlobalVariableProposals";
import { useHumanInputProposals } from "../../hooks/useHumanInputProposals";
import { useLoopTemplateProposals } from "../../hooks/useLoopTemplateProposals";
import { workItemService } from "../../services/auth";
import EditProposalCard from "../EditProposalCard";
import GlobalVariableProposalCard from "../GlobalVariableProposalCard";
import HumanInputProposalCard from "../HumanInputProposalCard";
import LoopTemplateProposalCard from "../LoopTemplateProposalCard";
import { placeActionProposals } from "../editProposalPlacement";
import MarkdownRenderer from "../MarkdownRenderer";
import LiveStream from "../NodeTimeline/LiveStream";
import HaltSteerControls from "./HaltSteerControls";
import Icon from "../Icon";
import { FeedbackBanner, PrView, QueuedPrWrites, feedbackFor } from "./panels";
import { variablesSetByTurn } from "./turnVariables";
import { useRunView } from "./useRunView";
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

/** Something that happened to the run (it started, waited, ended), rather than a turn in the dialogue. */
function RunEvent({ name, text, timestamp }: { name: string; text: string; timestamp: string }) {
  return (
    <div className="wiv2-run-event">
      <strong>{name}</strong>
      <span className="wiv2-run-event-text">{text}</span>
      <span>{new Date(timestamp).toLocaleString()}</span>
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

/** The current run's detail as last read, when that is the run `runId`. */
function currentRunIf(detail: WorkItemDetail, runId: string | null) {
  return runId && detail.currentRun?.id === runId ? detail.currentRun : null;
}

/**
 * Whether the thread's run has a pull request, or anything queued for one, to
 * show. What the item holds about a pull request is its current run's, so it
 * shows only when that is the thread's run.
 */
function hasPrDetails(workItem: WorkItem, detail: WorkItemDetail, runId: string | null): boolean {
  if (!runId || workItem.currentLoopRunId !== runId) return false;
  const run = currentRunIf(detail, runId);
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
  runId,
  onOpen,
}: {
  workItem: WorkItem;
  detail: WorkItemDetail;
  runId: string | null;
  onOpen: (panel: HTMLElement) => void;
}) {
  // null until the user opens or shuts it: the panel then starts open while
  // replies are queued, since the feedback asks the human to read them first.
  const [chosen, setChosen] = useState<boolean | null>(null);
  const panelRef = useRef<HTMLElement | null>(null);
  useEffect(() => {
    if (chosen && panelRef.current) onOpen(panelRef.current);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [chosen]);
  const run = currentRunIf(detail, runId);
  const snapshot = run?.prSnapshot ?? null;
  const pending = run?.prQueuedWrites?.length ?? 0;
  const open = chosen ?? pending > 0;
  // Only while the run still waits on the merge: a PR closed since, or a run
  // that has moved on, needs no one to merge it.
  const autoMergeFailed =
    run?.prAutoMerge === "IldFailed" &&
    run.status === LoopRunStatus.WaitingHuman &&
    snapshot?.state !== "closed";
  if (!hasPrDetails(workItem, detail, runId)) return null;
  return (
    <section ref={panelRef} className="wiv2-pr-details">
      <div className="wiv2-pr-details-header">
        <button
          type="button"
          className="wiv2-pr-details-toggle"
          aria-expanded={open}
          onClick={() => setChosen(!open)}
        >
          <Icon name="chevron" className={`wiv2-chevron ${open ? "wiv2-chevron-open" : ""}`} />
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
      {autoMergeFailed && (
        <p className="wiv2-pr-details-automerge-failed" role="alert">
          AutoMerge: ILD could not merge this PR ({run?.prAutoMergeError}). Please merge it
          manually.
        </p>
      )}
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

interface ActionThreadProps {
  workItem: WorkItem;
  detail: WorkItemDetail;
  feedbackPrompt: string | null;
  active: boolean;
  /** The global variable suggestion a link points at, scrolled to once its card shows. */
  revealGlobalProposalId?: string | null;
  /** Opens the Runs tab, where a failed node sits in its run's timeline. */
  onShowRuns?: () => void;
}

/**
 * The Action tab as one chronological thread for the item's latest run,
 * finished or not: its conversation so far, the live run as the latest AI
 * bubble, and the pending human feedback as the latest human bubble, with the
 * edit proposals and global variable suggestions its runs made after the turns
 * that made them. Opens scrolled to the newest entry, or to the suggestion a
 * link points at. Earlier runs are on the Runs tab.
 */
export default function ActionThread(props: ActionThreadProps) {
  const runId = props.workItem.latestLoopRunId ?? null;
  return <RunThread key={runId ?? ""} runId={runId} {...props} />;
}

function RunThread({
  runId,
  workItem,
  detail,
  feedbackPrompt,
  active,
  revealGlobalProposalId,
  onShowRuns,
}: ActionThreadProps & { runId: string | null }) {
  const view = useRunView(runId, detail.runLock.settledOf(runId), {
    isCurrent: !!runId && workItem.currentLoopRunId === runId,
    run: detail.currentRun,
  });
  const { messages } = view;
  const turnVariables = useTurnVariables(workItem.id, messages.length);
  const { proposals, refresh } = useEditProposals({ requestedByWorkItemId: workItem.id });
  const suggestions = useGlobalVariableProposals({ workItemId: workItem.id });
  const loopProposals = useLoopTemplateProposals({ workItemId: workItem.id });
  const humanInput = useHumanInputProposals(workItem.id);
  const threadRef = useRef<HTMLDivElement | null>(null);
  const pinnedToBottom = useRef(true);
  const savedScrollTop = useRef(0);
  const awaitingHuman = feedbackFor(workItem, runId, view.run) !== null;
  // The live output is the current run's, so it belongs here only while that is this run.
  const streaming = detail.shouldStream && workItem.currentLoopRunId === runId;

  // Stay on the newest entry while content loads in and grows beneath it
  // (prompt, PR snapshot, live output), until the reader scrolls away. Coming
  // back to the tab returns to where the reader left it: the bottom if they
  // were following it, otherwise the same place.
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
      savedScrollTop.current = scroller.scrollTop;
    };
    if (pinnedToBottom.current) toBottom();
    else scroller.scrollTop = savedScrollTop.current;
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

  const { afterTurn, live, end } = placeActionProposals(runId, messages, [
    ...(proposals ?? []).map((proposal) => ({
      ...proposal,
      card: (
        <EditProposalCard key={`proposal:${proposal.id}`} proposal={proposal} onSettled={refresh} />
      ),
    })),
    ...(suggestions.proposals ?? []).map((proposal) => ({
      ...proposal,
      card: (
        <GlobalVariableProposalCard
          key={`gvproposal:${proposal.id}`}
          proposal={proposal}
          onSettled={suggestions.refresh}
          // Revealed once the turns it follows are in place, so none lands above it afterwards.
          reveal={
            view.conversationRead && proposal.id === revealGlobalProposalId
              ? revealFromTop
              : undefined
          }
        />
      ),
    })),
    ...(loopProposals.proposals ?? []).map((proposal) => ({
      ...proposal,
      card: (
        <LoopTemplateProposalCard
          key={`ltproposal:${proposal.id}`}
          proposal={proposal}
          onSettled={loopProposals.refresh}
        />
      ),
    })),
  ]);
  const cards = (list: { card: ReactNode }[] = []) => list.map((placed) => placed.card);
  // About the question this run is parked on, so by the run they answer and not
  // by the step that made them: shown just before the buttons that answer it.
  const answerSuggestions = (humanInput.proposals ?? [])
    .filter((proposal) => proposal.runId === runId)
    .sort((a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt));

  const isEmpty =
    messages.length === 0 &&
    !view.error &&
    !streaming &&
    !awaitingHuman &&
    !detail.movedOn &&
    !hasPrDetails(workItem, detail, runId) &&
    live.length === 0 &&
    end.length === 0 &&
    answerSuggestions.length === 0;

  // One keyed list, so a card that moves to a new slot (its step's turn
  // arriving) is moved rather than remounted, and keeps a decision in progress.
  const entries: ReactNode[] = [];
  messages.forEach((m, i) => {
    if (m.role === "system") {
      entries.push(
        <RunEvent key={`turn:${m.id}`} name={m.name} text={m.text} timestamp={m.timestamp} />,
      );
    } else {
      const variables = variablesSetByTurn(m, turnVariables);
      entries.push(
        <Bubble
          key={`turn:${m.id}`}
          side={m.role}
          author={m.role === "ai" ? m.name : undefined}
          timestamp={m.timestamp}
          footer={variables.length > 0 && <TurnVariables variables={variables} />}
        >
          <div className="conversation-message-content">
            {m.role === "human" && !m.text ? "No comment" : <MarkdownRenderer content={m.text} />}
          </div>
        </Bubble>,
      );
    }
    entries.push(...cards(afterTurn.get(i)));
  });
  // Halt, steer and cleanup act on this run, and wait for any other action on
  // it, wherever in the dialog that was started.
  entries.push(
    <Bubble key="live" side="ai" author="AI" live={streaming}>
      {streaming && <LiveStream text={detail.progressText} />}
      <HaltSteerControls
        run={view.run}
        workItemStatus={workItem.status}
        onHalt={() => detail.runLock.hold(runId, "halt", () => detail.handleHalt(runId))}
        onResumeSteer={(note) =>
          detail.runLock.hold(runId, "steer", () => detail.handleResumeSteer(runId, note))
        }
        onCleanupDone={() => detail.runLock.hold(runId, "abandon", detail.handleCleanupDone)}
        onCleanupBacklog={() => detail.runLock.hold(runId, "abandon", detail.handleCleanupBacklog)}
        showAbandon={false}
        blocked={detail.runLock.pendingOf(runId) !== null}
      />
    </Bubble>,
    ...cards(live),
    <PrDetails
      key="pr-details"
      workItem={workItem}
      detail={detail}
      runId={runId}
      onOpen={revealFromTop}
    />,
    ...answerSuggestions.map((proposal) => (
      <HumanInputProposalCard
        key={`hiproposal:${proposal.id}`}
        proposal={proposal}
        decidable={proposal.runNodeId === view.run?.waitingRunNodeId}
        onSettled={humanInput.refresh}
      />
    )),
    <Bubble key="feedback" side="human">
      <FeedbackBanner
        workItem={workItem}
        runId={runId}
        run={view.run}
        detail={detail}
        prompt={feedbackPrompt}
        onShowRuns={onShowRuns}
      />
    </Bubble>,
    ...cards(end),
  );

  return (
    <div className="wiv2-thread" ref={threadRef}>
      {entries}
      {detail.movedOn && (
        <div className="preview-message preview-error wiv2-moved-on" role="alert">
          <p>The run moved on before your feedback was sent, so it was not delivered.</p>
          {detail.movedOn.text && (
            <blockquote className="wiv2-moved-on-text">{detail.movedOn.text}</blockquote>
          )}
          <button
            type="button"
            className="btn btn-sm btn-secondary"
            onClick={detail.dismissMovedOn}
          >
            Dismiss
          </button>
        </div>
      )}
      {view.error && (
        <div className="preview-message preview-error">
          The conversation could not be loaded: {view.error}
        </div>
      )}
      {isEmpty && <div className="wiv2-empty">No action required.</div>}
    </div>
  );
}
