import { useEffect, useMemo, useRef, useState } from "react";
import { useSignalR } from "../../../hooks/useSignalR";
import { loggingService } from "../../../services/auth";
import type { LogEntry } from "../../../types";
import { Segmented, SettingRow, Switch } from "../controls";

const TAKE = 200;

/** Long enough that typing a word is one query rather than one per letter. */
const TYPING_SETTLES_MS = 250;

const LEVELS = ["Debug", "Information", "Warning", "Error"] as const;
type Level = (typeof LEVELS)[number];

const LEVEL_TAG: Record<Level, string> = {
  Debug: "DBG",
  Information: "INF",
  Warning: "WRN",
  Error: "ERR",
};

const LEVEL_HELP: Record<Level, string> = {
  Debug: "Everything, including each node transition and every proxy decision. Noisy.",
  Information: "Runs, nodes and requests as they happen. The default.",
  Warning: "Only what went wrong or nearly did.",
  Error: "Only failures.",
};

interface LogLine {
  id: number;
  timestamp: string;
  level: Level;
  source: string;
  message: string;
  detail?: string;
}

function isLevel(value: string): value is Level {
  return (LEVELS as readonly string[]).includes(value);
}

/**
 * Serilog writes six levels where the page shows four: Verbose reads as Debug
 * and Fatal as Error rather than as a level with no tag and no colour.
 */
function toLevel(value: string): Level {
  if (isLevel(value)) return value;
  if (value === "Verbose") return "Debug";
  if (value === "Fatal") return "Error";
  return "Information";
}

/**
 * The floor to ask the backend for. Serilog's Verbose shows here as Debug, so a
 * minimum of Debug has to let it through or the page hides a line it would then
 * have labelled DBG.
 */
const SERVER_FLOOR: Record<Level, string> = {
  Debug: "Verbose",
  Information: "Information",
  Warning: "Warning",
  Error: "Error",
};

function toLine(entry: LogEntry): LogLine {
  return {
    id: entry.id,
    timestamp: entry.timestamp,
    level: toLevel(entry.level),
    source: entry.source,
    message: entry.message,
    detail: entry.detail ?? undefined,
  };
}

/** The backend's log level, and a view of what it has been writing. */
export default function LoggingSettings() {
  const { on, off } = useSignalR();
  // Read rather than assumed: the level lives in a switch the API can report,
  // so this page never claims one the backend is not actually logging at.
  const [level, setLevel] = useState<Level | null>(null);
  const [startupLevel, setStartupLevel] = useState<Level | null>(null);
  const [levelError, setLevelError] = useState<string | null>(null);
  const [lines, setLines] = useState<LogLine[]>([]);
  const [logError, setLogError] = useState<string | null>(null);
  const [minLevel, setMinLevel] = useState<"All" | Level>("All");
  const [search, setSearch] = useState("");
  const [settledSearch, setSettledSearch] = useState("");
  const [tailing, setTailing] = useState(true);
  const [expanded, setExpanded] = useState<number | null>(null);
  const changed = useRef(false);

  useEffect(() => {
    const timer = setTimeout(() => setSettledSearch(search.trim()), TYPING_SETTLES_MS);
    return () => clearTimeout(timer);
  }, [search]);

  // Both filters are the backend's to apply, following or not: it holds more
  // lines than the page asks for, so a filter only reaches the ones that have
  // scrolled off if it goes over the whole buffer. Resuming re-reads as well,
  // or whatever was written while following was off stays missing from the
  // view; pausing on its own does not, since that would jump the list it has
  // just frozen.
  const following = useRef(tailing);
  useEffect(() => {
    const pausing = following.current && !tailing;
    following.current = tailing;
    if (pausing) return;

    let stale = false;
    void loggingService
      .getEntries({
        take: TAKE,
        minimumLevel: minLevel === "All" ? undefined : SERVER_FLOOR[minLevel],
        search: settledSearch || undefined,
      })
      .then((entries) => {
        if (stale) return;
        setLines(entries.map(toLine));
        setLogError(null);
      })
      .catch(() => {
        if (!stale) setLogError("Failed to read the log.");
      });
    return () => {
      stale = true;
    };
  }, [tailing, minLevel, settledSearch]);

  useEffect(() => {
    if (!tailing) return;
    const onAppended = (message: { payload: LogEntry }) => {
      const line = toLine(message.payload);
      setLines((prev) => [line, ...prev.filter((l) => l.id !== line.id)].slice(0, TAKE));
    };
    on("LogEntryAppended", onAppended);
    return () => off("LogEntryAppended", onAppended);
  }, [on, off, tailing]);

  useEffect(() => {
    void loggingService
      .getLevel()
      .then((status) => {
        if (isLevel(status.startupLevel)) setStartupLevel(status.startupLevel);
        // A click that beat this read already set the level, and its own PUT
        // is the newer truth; the stale read must not put the old one back.
        if (!changed.current && isLevel(status.level)) setLevel(status.level);
      })
      // Unreachable API: press nothing rather than guess a level.
      .catch(() => {});
  }, []);

  const changeLevel = async (next: Level) => {
    changed.current = true;
    const previous = level;
    setLevel(next);
    setLevelError(null);
    try {
      await loggingService.setLevel(next);
    } catch {
      setLevel(previous);
      setLevelError("Failed to change the level.");
    }
  };

  const overriding = level !== null && startupLevel !== null && level !== startupLevel;

  const filtering = minLevel !== "All" || search.trim() !== "";

  // The same filter the backend just applied, over lines that have arrived
  // since: a live one has to be judged by it too, and it never went through the
  // query that fetched the rest.
  const visible = useMemo(() => {
    const needle = search.trim().toLowerCase();
    const floor = minLevel === "All" ? -1 : LEVELS.indexOf(minLevel);
    return lines.filter(
      (line) =>
        LEVELS.indexOf(line.level) >= floor &&
        (!needle ||
          line.message.toLowerCase().includes(needle) ||
          line.source.toLowerCase().includes(needle)),
    );
  }, [lines, minLevel, search]);

  return (
    <>
      <div className="settings-pane-header">
        <h2>Logging</h2>
        <p>How much the backend writes down, and what it has written.</p>
      </div>

      <section className="settings-card">
        <div className="settings-card-header">
          <h3 className="settings-card-title">Level</h3>
        </div>
        <SettingRow
          label="Log level override"
          help={
            <>
              {level && <>{LEVEL_HELP[level]} </>}
              {overriding ? (
                <>
                  Overriding <code>ILD_LOG_LEVEL</code>, which is <strong>{startupLevel}</strong>.
                  The override applies immediately and lasts until the backend restarts — nothing is
                  written down, so a restart goes back to <strong>{startupLevel}</strong>.
                </>
              ) : (
                <>
                  Following <code>ILD_LOG_LEVEL</code>. Picking a different level overrides it for
                  the running process only; a restart comes back here.
                </>
              )}
              {levelError && <span className="settings-error"> {levelError}</span>}
            </>
          }
        >
          <Segmented
            label="Log level override"
            value={level}
            onChange={(v) => void changeLevel(v)}
            options={LEVELS.map((l) => ({ value: l, label: l }))}
          />
          {overriding && startupLevel && (
            <button type="button" className="btn" onClick={() => void changeLevel(startupLevel)}>
              Reset
            </button>
          )}
        </SettingRow>
      </section>

      <section className="settings-card">
        <div className="settings-card-header">
          <h3 className="settings-card-title">Log</h3>
          <label className="log-tail">
            <Switch checked={tailing} onChange={setTailing} label="Follow the log live" />
            Follow live
          </label>
        </div>
        <p className="settings-card-note">
          The most recent lines, held in memory by the backend process that answered: a restart
          clears them, and if you run more than one backend you are reading one of them.
        </p>
        <div className="log-toolbar">
          <input
            type="search"
            className="settings-input"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Filter by message or source"
            aria-label="Filter the log"
          />
          <Segmented
            label="Minimum level"
            value={minLevel}
            onChange={setMinLevel}
            options={[
              { value: "All" as const, label: "All" },
              ...LEVELS.map((l) => ({ value: l, label: l })),
            ]}
          />
        </div>
        {logError && <div className="settings-error">{logError}</div>}
        {visible.length === 0 ? (
          <p className="settings-card-note">
            {filtering ? "Nothing matches that filter." : "Nothing written yet."}
          </p>
        ) : (
          <ul className="log-lines">
            {visible.map((line) => (
              <li key={line.id} className={`log-line log-line-${line.level.toLowerCase()}`}>
                <button
                  type="button"
                  className="log-line-main"
                  onClick={() => setExpanded(expanded === line.id ? null : line.id)}
                  aria-expanded={expanded === line.id}
                >
                  <span className="log-caret" aria-hidden="true">
                    {expanded === line.id ? "▾" : "▸"}
                  </span>
                  <span className="log-time">
                    {new Date(line.timestamp).toLocaleTimeString(undefined, { hour12: false })}
                  </span>
                  <span className={`log-level log-level-${line.level.toLowerCase()}`}>
                    {LEVEL_TAG[line.level]}
                  </span>
                  <span className="log-source">{line.source}</span>
                  <span className="log-message">{line.message}</span>
                </button>
                {expanded === line.id && (
                  <dl className="log-detail">
                    <dt>Time</dt>
                    <dd>{new Date(line.timestamp).toLocaleString()}</dd>
                    <dt>Level</dt>
                    <dd>{line.level}</dd>
                    <dt>Source</dt>
                    <dd>{line.source}</dd>
                    <dt>Message</dt>
                    <dd>{line.message}</dd>
                    {line.detail && (
                      <>
                        <dt>Detail</dt>
                        <dd>
                          <pre>{line.detail}</pre>
                        </dd>
                      </>
                    )}
                  </dl>
                )}
              </li>
            ))}
          </ul>
        )}
      </section>

      <style>{`
        .log-tail { display: flex; align-items: center; gap: 0.5rem; font-size: 0.78rem; color: #a0a0b0; }
        .log-toolbar { display: flex; gap: 0.75rem; align-items: center; margin-bottom: 0.75rem; flex-wrap: wrap; }
        .log-toolbar input[type="search"] { flex: 1; min-width: 12rem; }
        .log-lines {
          list-style: none;
          margin: 0;
          padding: 0;
          max-height: 26rem;
          overflow-y: auto;
          background-color: #16162a;
          border: 1px solid #2d2d44;
          border-radius: 0.375rem;
        }
        .log-line { border-bottom: 1px solid #22223a; }
        .log-line:last-child { border-bottom: none; }
        .log-line-main {
          display: grid;
          grid-template-columns: 0.9rem 5rem 3rem 14rem minmax(0, 1fr);
          gap: 0.6rem;
          width: 100%;
          text-align: left;
          background: none;
          border: none;
          cursor: pointer;
          padding: 0.3rem 0.6rem;
          font-family: monospace;
          font-size: 0.75rem;
          color: #c0c0d0;
        }
        .log-line-main:hover { background-color: #1e1e30; }
        .log-time { color: #707090; }
        .log-source { color: #8a8ab0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .log-message { white-space: pre-wrap; word-break: break-word; }
        .log-level { font-weight: 600; }
        .log-level-debug { color: #707090; }
        .log-level-information { color: #60a5fa; }
        .log-level-warning { color: #fbbf24; }
        .log-level-error { color: #f87171; }
        .log-line-error .log-message { color: #fca5a5; }
        .log-caret { color: #707090; }
        .log-detail {
          display: grid;
          grid-template-columns: 5rem minmax(0, 1fr);
          gap: 0.3rem 0.75rem;
          margin: 0;
          padding: 0.6rem 0.75rem 0.75rem 1.5rem;
          background-color: #1a1a2e;
          border-top: 1px solid #22223a;
          font-size: 0.73rem;
        }
        .log-detail dt { color: #707090; }
        .log-detail dd { margin: 0; color: #c0c0d0; word-break: break-word; }
        .log-detail pre {
          margin: 0;
          white-space: pre-wrap;
          font-size: 0.72rem;
          color: #a0a0b0;
        }
      `}</style>
    </>
  );
}
