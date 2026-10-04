import { useEffect, useState } from "react";
import {
  EventLogEntry,
  LoopRun,
  LoopRunAvailableSession,
  LoopRunSessionPreview,
  LoopRunVariable,
} from "../../types";
import { loopRunService } from "../../services/auth";
import MarkdownRenderer from "../MarkdownRenderer";
import NodeEventsSection from "../NodeTimeline/NodeEventsSection";

// The API client rejects with a plain { status, message } object, not an Error.
export function failureMessage(error: unknown, fallback: string): string {
  const message = (error as { message?: unknown } | null)?.message;
  return typeof message === "string" && message ? message : fallback;
}

function formatTimestamp(ts: string | null): string {
  return ts ? new Date(ts).toLocaleString() : "Unknown";
}

function formatSessionJson(sessionJson: string): string {
  try {
    return JSON.stringify(JSON.parse(sessionJson), null, 2);
  } catch {
    return sessionJson;
  }
}

function buildSessionSummary(preview: LoopRunSessionPreview): string[] {
  const lines = [
    `Adapter: ${preview.adapterName}`,
    `Session: ${preview.sessionId}`,
    `Updated: ${formatTimestamp(preview.updatedAt ?? preview.createdAt)}`,
  ];
  try {
    const parsed = JSON.parse(preview.sessionJson) as Record<string, unknown>;
    if (Array.isArray(parsed.messages)) lines.push(`Messages: ${parsed.messages.length}`);
    if (typeof parsed.id === "string") lines.push(`Snapshot Id: ${parsed.id}`);
    if (Array.isArray(parsed.tools)) lines.push(`Tools: ${parsed.tools.length}`);
    lines.push(`Root: ${Array.isArray(parsed) ? "array" : typeof parsed}`);
  } catch {
    lines.push("Root: raw text");
  }
  return lines;
}

/** A section that shows only its title until the user expands it. */
function Collapsible({
  title,
  onOpen,
  children,
}: {
  title: string;
  onOpen?: () => void;
  children: React.ReactNode;
}) {
  const [open, setOpen] = useState(false);
  return (
    <div className="wiv2-collapsible">
      <button
        type="button"
        className="wiv2-collapsible-toggle"
        aria-expanded={open}
        onClick={() => {
          if (!open) onOpen?.();
          setOpen(!open);
        }}
      >
        <span className="wiv2-node-chevron">{open ? "▾" : "▸"}</span>
        {title}
      </button>
      {open && <div className="wiv2-collapsible-body">{children}</div>}
    </div>
  );
}

/** The loop variables a run wrote, for referencing as {{Var.<name>}}. */
export function RunVariables({ variables }: { variables: LoopRunVariable[] }) {
  if (variables.length === 0) return null;
  return (
    <Collapsible title={`Variables (${variables.length})`}>
      <div className="wiv2-run-hint">
        Loop variables written during this run. Reference them in node templates as{" "}
        {"{{Var.<name>}}"}.
      </div>
      {variables.map((variable) => (
        <div key={variable.name} className="wiv2-run-entry">
          <div className="wiv2-run-entry-head">
            <span className="wiv2-run-entry-name">{variable.name}</span>
            <span className="wiv2-run-entry-meta">
              Updated: {formatTimestamp(variable.updatedAt ?? variable.createdAt)}
            </span>
          </div>
          <MarkdownRenderer content={variable.value} />
        </div>
      ))}
    </Collapsible>
  );
}

/**
 * The run's saved AI adapter sessions, each previewable inline. Its owner is
 * keyed by run, so a preview never outlives the run it was opened for.
 */
export function RunSessions({
  runId,
  sessions,
}: {
  runId: string;
  sessions: LoopRunAvailableSession[];
}) {
  const [preview, setPreview] = useState<LoopRunSessionPreview | null>(null);
  const [loadingKey, setLoadingKey] = useState<string | null>(null);
  const [errorText, setErrorText] = useState("");

  if (sessions.length === 0) return null;

  const openPreview = async (session: LoopRunAvailableSession) => {
    const key = `${session.adapterName}:${session.sessionId}`;
    setErrorText("");
    setLoadingKey(key);
    try {
      setPreview(
        await loopRunService.getSessionPreview(runId, session.adapterName, session.sessionId),
      );
    } catch (error) {
      setErrorText(failureMessage(error, "Failed to load session preview."));
    } finally {
      setLoadingKey(null);
    }
  };

  return (
    <Collapsible title={`AI sessions (${sessions.length})`}>
      <div className="wiv2-run-hint">
        Saved adapter sessions for this run. Use these ids when routing an AI node to a specific
        session.
      </div>
      {errorText && (
        <div className="wiv2-error" role="alert">
          {errorText}
          <button type="button" className="wiv2-error-close" onClick={() => setErrorText("")}>
            ✕
          </button>
        </div>
      )}
      {sessions.map((session) => {
        const key = `${session.adapterName}:${session.sessionId}`;
        return (
          <div key={key} className="wiv2-run-entry">
            <div className="wiv2-run-entry-head">
              <span className="wiv2-run-entry-name">{session.adapterName}</span>
              {session.isCurrent && <span className="wiv2-run-entry-current">Current</span>}
              <span className="wiv2-run-entry-meta">
                Updated: {formatTimestamp(session.updatedAt ?? session.createdAt)}
              </span>
              <button
                type="button"
                className="btn btn-sm btn-secondary"
                onClick={() => void openPreview(session)}
                disabled={loadingKey !== null}
                aria-label={`Preview session ${session.sessionId}`}
              >
                {loadingKey === key ? "Loading…" : "Preview"}
              </button>
            </div>
            <code className="wiv2-run-entry-id">{session.sessionId}</code>
            {session.placeholders.length > 0 && (
              <div className="wiv2-run-entry-meta">
                Placeholders: {session.placeholders.join(", ")}
              </div>
            )}
          </div>
        );
      })}
      {preview && (
        <div className="wiv2-session-preview" role="region" aria-label="Session preview">
          <div className="wiv2-run-entry-head">
            <span className="wiv2-run-entry-name">Session preview</span>
            <button
              type="button"
              className="btn btn-sm btn-secondary"
              onClick={() => setPreview(null)}
            >
              Close
            </button>
          </div>
          <ul className="wiv2-session-summary">
            {buildSessionSummary(preview).map((line) => (
              <li key={line}>{line}</li>
            ))}
          </ul>
          <pre className="wiv2-node-pre">{formatSessionJson(preview.sessionJson)}</pre>
        </div>
      )}
    </Collapsible>
  );
}

async function readRunEvents(runId: string, cancelled: () => boolean): Promise<EventLogEntry[]> {
  const entries: EventLogEntry[] = [];
  let cursor = 0;
  for (;;) {
    const page = await loopRunService.getEvents(runId, cursor, 500);
    entries.push(...page.entries);
    if (!page.hasMore || cancelled()) return entries;
    cursor = page.nextCursor;
  }
}

export interface RunEvents {
  /** The shown run's whole event log; null until it has been read. */
  entries: EventLogEntry[] | null;
  errorText: string;
  /**
   * Asks for the log; every Events section calls it when opened. Only the
   * first call for a run reads, unless the last read failed: then it retries.
   */
  request: () => void;
}

/**
 * A run's event log, read once the first of its nodes' Events sections is
 * opened and shared by all of them. It is read again only when the run has
 * moved on (its status or node rows changed), so a finished run is read once,
 * or when a section is opened again after a failed read. Its owner is keyed by
 * run, so the log never outlives the run it was read for.
 */
export function useRunEvents(run: LoopRun | null): RunEvents {
  const [wanted, setWanted] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const [read, setRead] = useState<{ entries: EventLogEntry[] | null; errorText: string } | null>(
    null,
  );
  const runId = run?.id ?? null;
  const progress = run
    ? `${run.status}|${run.nodes.length}|${run.nodes[run.nodes.length - 1]?.status ?? ""}`
    : null;

  useEffect(() => {
    if (!wanted || !runId) return;
    let cancelled = false;
    readRunEvents(runId, () => cancelled).then(
      (entries) => {
        if (!cancelled) setRead({ entries, errorText: "" });
      },
      (error: unknown) => {
        if (!cancelled)
          setRead({ entries: null, errorText: failureMessage(error, "Failed to load events.") });
      },
    );
    return () => {
      cancelled = true;
    };
  }, [wanted, runId, progress, attempt]);

  return {
    entries: read?.entries ?? null,
    errorText: read?.errorText ?? "",
    request: () => {
      setWanted(true);
      if (read?.errorText) {
        setRead(null);
        setAttempt((n) => n + 1);
      }
    },
  };
}

/** One node execution's events, from its run's shared log, once expanded. */
export function NodeEvents({ runNodeId, events }: { runNodeId: string; events: RunEvents }) {
  return (
    <Collapsible title="Events" onOpen={events.request}>
      {events.errorText ? (
        <div className="wiv2-node-error">{events.errorText}</div>
      ) : events.entries ? (
        <NodeEventsSection events={events.entries.filter((e) => e.runNodeId === runNodeId)} />
      ) : (
        <div className="wiv2-empty">Loading events…</div>
      )}
    </Collapsible>
  );
}
