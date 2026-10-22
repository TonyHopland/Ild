import { useEffect, useState } from "react";
import { Link } from "react-router";
import type { WorkItem, WorkItemHistoryEntry } from "../../types";
import { workItemService } from "../../services/auth";
import { formatRelativeTime } from "../../utils/relativeTime";
import { DescriptionDiff, TagsChange } from "../FieldChanges";
import "./HistoryPanel.css";

const FIELD_LABELS: Record<string, string> = {
  title: "Title",
  description: "Description",
  status: "Status",
  priority: "Priority",
  tags: "Tags",
  dependencies: "Dependencies",
  repository: "Repository",
  aiProviderOverride: "AI provider override",
  aiProviderOverrideId: "AI provider",
  branchNameOverride: "Branch name",
  baseBranchOverride: "Base branch",
  "pullRequest.linked": "Pull request linked",
  "pullRequest.merged": "Pull request merged",
  deleted: "Title",
};

/** Events say what happened, not which field changed. */
const EVENT_PHRASES: Record<string, string> = {
  "pullRequest.linked": "linked a pull request",
  "pullRequest.merged": "merged a pull request",
};

const isPullRequestEvent = (field: string) =>
  field === "pullRequest.linked" || field === "pullRequest.merged";

/** A field this view has no name for, from a tracker that keeps more, reads as its own name. */
/** A tracker may name a field anything, `constructor` included, so only own keys count. */
const lookup = (table: Record<string, string>, key: string) =>
  Object.prototype.hasOwnProperty.call(table, key) ? table[key] : undefined;

const fieldLabel = (field: string) => lookup(FIELD_LABELS, field) ?? field;

/** A JSON array of strings, or null when the value is not one, which is then shown as it is. */
function parseList(value: string | null): string[] | null {
  if (value === null) return [];
  try {
    const parsed: unknown = JSON.parse(value);
    return Array.isArray(parsed) && parsed.every((v) => typeof v === "string") ? parsed : null;
  } catch {
    return null;
  }
}

function Value({ value }: { value: string | null }) {
  return value === null || value === "" ? (
    <span className="wiv2-history-none">none</span>
  ) : (
    <span>{value}</span>
  );
}

function ListChange({ before, after }: { before: string | null; after: string | null }) {
  const oldList = parseList(before);
  const newList = parseList(after);
  if (oldList === null || newList === null) return <OldToNew before={before} after={after} />;
  if (oldList.length === 0 && newList.length === 0) return <Value value={null} />;
  return <TagsChange before={oldList} after={newList} />;
}

function OldToNew({ before, after }: { before: string | null; after: string | null }) {
  return (
    <span className="wiv2-history-values">
      <span className="wiv2-history-old">
        <Value value={before} />
      </span>{" "}
      <span role="img" aria-label="to">
        →
      </span>{" "}
      <span className="wiv2-history-new">
        <Value value={after} />
      </span>
    </span>
  );
}

function PullRequestLink({ url }: { url: string }) {
  return /^https?:\/\//i.test(url) ? (
    <a href={url} target="_blank" rel="noopener noreferrer">
      {url}
    </a>
  ) : (
    <span>{url}</span>
  );
}

/**
 * One field of a save: the change it made, or for a Created, Baseline or Deleted
 * save its value. An event has no earlier value to compare with.
 */
function FieldChange({
  entry,
  repositoryName,
}: {
  entry: WorkItemHistoryEntry;
  repositoryName?: (id: string) => string | null;
}) {
  const change = entry.kind === "Changed";
  // A repository is recorded by id; it is shown by name, as the Overview does.
  if (entry.field === "repository" && repositoryName) {
    // Until the repositories have loaded (or if they could not be), a recorded
    // repository shows as "…": it was there, its name is not known yet. "none"
    // is kept for a change that really had no repository.
    const name = (id: string | null) => (id ? (repositoryName(id) ?? "…") : null);
    return change ? (
      <OldToNew before={name(entry.oldValue)} after={name(entry.newValue)} />
    ) : (
      <Value value={name(entry.newValue)} />
    );
  }
  if (isPullRequestEvent(entry.field)) return <PullRequestLink url={entry.newValue ?? ""} />;
  if (entry.field === "description")
    return change ? (
      <DescriptionDiff before={entry.oldValue ?? ""} after={entry.newValue ?? ""} />
    ) : (
      <div className="wiv2-history-text">{entry.newValue}</div>
    );
  if (entry.field === "tags" || entry.field === "dependencies")
    return <ListChange before={change ? entry.oldValue : entry.newValue} after={entry.newValue} />;
  return change ? (
    <OldToNew before={entry.oldValue} after={entry.newValue} />
  ) : (
    <Value value={entry.newValue} />
  );
}

/** A Created or Baseline save lists every tracked field; the unset ones say nothing. */
function isUnset(entry: WorkItemHistoryEntry) {
  return entry.newValue === null || entry.newValue === "" || entry.newValue === "[]";
}

interface Save {
  saveId: string;
  entries: WorkItemHistoryEntry[];
}

function groupBySave(entries: WorkItemHistoryEntry[]): Save[] {
  const saves = new Map<string, Save>();
  for (const entry of entries) {
    const save = saves.get(entry.saveId) ?? { saveId: entry.saveId, entries: [] };
    save.entries.push(entry);
    saves.set(entry.saveId, save);
  }
  return [...saves.values()];
}

/** "changed title, description and linked a pull request": the fields first, then the events. */
function describeChanges(entries: WorkItemHistoryEntry[]): string {
  const fields = entries.filter((e) => !lookup(EVENT_PHRASES, e.field));
  const parts = [
    ...(fields.length > 0
      ? [`changed ${fields.map((e) => fieldLabel(e.field).toLowerCase()).join(", ")}`]
      : []),
    ...entries.flatMap((e) => lookup(EVENT_PHRASES, e.field) ?? []),
  ];
  return parts.join(" and ");
}

function SaveEvent({
  save,
  now,
  repositoryName,
}: {
  save: Save;
  now: number;
  repositoryName?: (id: string) => string | null;
}) {
  const first = save.entries[0];
  const shown = first.kind === "Changed" ? save.entries : save.entries.filter((e) => !isUnset(e));
  const what =
    first.kind === "Baseline"
      ? first.username
        ? "— state at history start"
        : "State at history start"
      : first.kind === "Created"
        ? "created the item"
        : first.kind === "Deleted"
          ? "deleted the item"
          : describeChanges(shown);
  return (
    <li className={`wiv2-history-save wiv2-history-save--${first.kind.toLowerCase()}`}>
      <div className="wiv2-history-head">
        {first.username && <strong className="wiv2-history-user">{first.username}</strong>}{" "}
        <span className="wiv2-history-what">{what}</span>{" "}
        <time
          className="wiv2-history-time"
          dateTime={first.timestamp}
          title={new Date(first.timestamp).toLocaleString()}
        >
          {formatRelativeTime(first.timestamp, now)}
        </time>
      </div>
      {(first.actorKind || first.actorDetail || first.actorWorkItemId) && (
        <div className="wiv2-history-actor">
          {first.actorKind && <span className="wiv2-history-kind">{first.actorKind}</span>}{" "}
          {first.actorDetail && <span>{first.actorDetail}</span>}{" "}
          {first.actorWorkItemId && (
            <span>
              working on{" "}
              <Link to={`/taskboard/${first.actorWorkItemId}`}>#{first.actorWorkItemId}</Link>
            </span>
          )}
        </div>
      )}
      <dl className="wiv2-history-fields">
        {shown.map((entry) => (
          <div key={entry.field} className="wiv2-history-field">
            <dt>{fieldLabel(entry.field)}</dt>
            <dd>
              <FieldChange entry={entry} repositoryName={repositoryName} />
              {entry.reason && <div className="wiv2-history-reason">{entry.reason}</div>}
            </dd>
          </div>
        ))}
      </dl>
    </li>
  );
}

interface HistoryState {
  entries: WorkItemHistoryEntry[] | null;
  loading: boolean;
  error: string | null;
}

/**
 * The record of what changed on the item, by whom and why: one entry per save,
 * newest first. Read when the tab is first opened and again whenever the item
 * changes. The owner keys it by work item, so another item gets a fresh panel;
 * a reply to a read that a newer read or an unmount has overtaken is dropped.
 */
export default function HistoryPanel({
  workItem,
  active,
  repositoryName,
}: {
  workItem: WorkItem;
  active: boolean;
  /** A repository id in words (see utils/repositoryName), or null until the list has loaded. */
  repositoryName?: (id: string) => string | null;
}) {
  const [opened, setOpened] = useState(active);
  if (active && !opened) setOpened(true);
  const [state, setState] = useState<HistoryState>({ entries: null, loading: false, error: null });

  useEffect(() => {
    if (!opened) return;
    let current = true;
    setState((s) => ({ ...s, loading: true }));
    workItemService.getHistory(workItem.id).then(
      (entries) => {
        if (current) setState({ entries, loading: false, error: null });
      },
      (err: unknown) => {
        if (current)
          setState((s) => ({
            ...s,
            loading: false,
            error: err instanceof Error ? err.message : String(err),
          }));
      },
    );
    return () => {
      current = false;
    };
  }, [opened, workItem.id, workItem.updatedAt]);

  const now = Date.now();
  return (
    <div className="wiv2-history" aria-busy={state.loading}>
      {state.error && (
        <p role="alert" className="wiv2-history-error">
          Could not load the history: {state.error}
        </p>
      )}
      {state.entries === null ? (
        state.loading && (
          <p role="status" className="wiv2-history-empty">
            Loading history…
          </p>
        )
      ) : state.entries.length === 0 ? (
        <p className="wiv2-history-empty">No history recorded for this item yet.</p>
      ) : (
        <ol className="wiv2-history-list">
          {groupBySave(state.entries).map((save) => (
            <SaveEvent key={save.saveId} save={save} now={now} repositoryName={repositoryName} />
          ))}
        </ol>
      )}
    </div>
  );
}
