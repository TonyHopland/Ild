import { useCallback, useEffect, useId, useRef, useState } from "react";
import ConnectionTest from "../../../components/ConnectionTest";
import { packageFeedService } from "../../../services/auth";
import type { ApiError, PackageFeed } from "../../../types";
import { SettingRow } from "../controls";

function describeError(err: unknown, fallback: string): string {
  const message = (err as ApiError | Error | null)?.message;
  return typeof message === "string" && message ? message : fallback;
}

/**
 * The open form: a new feed, or an existing one. `key` is fresh for every opening,
 * so each opening is its own form instance with its own draft.
 */
type Form = { kind: "new" } | { kind: "edit"; feed: PackageFeed };
type Editing = Form & { key: number };

interface FeedFormProps {
  form: Form;
  onSaved: () => Promise<void>;
  onCancel: () => void;
}

/**
 * Adds a feed, or edits one. Keyed per opening, so its draft and busy
 * flag never outlive it. The PAT field starts empty every time: the stored PAT
 * cannot be read back, and leaving it empty on an edit keeps it.
 */
function FeedForm({ form, onSaved, onCancel }: FeedFormProps) {
  const field = useId();
  const existing = form.kind === "edit" ? form.feed : null;
  const [name, setName] = useState(existing?.name ?? "");
  const [feedUrl, setFeedUrl] = useState(existing?.feedUrl ?? "");
  const [pat, setPat] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const save = async () => {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      if (existing) await packageFeedService.update(existing.id, { feedUrl, pat });
      else await packageFeedService.create({ name, feedUrl, pat });
      await onSaved();
    } catch (err) {
      setError(describeError(err, "Failed to save the feed."));
      setBusy(false);
    }
  };

  return (
    <section className="settings-card">
      <div className="settings-card-header">
        <h3 className="settings-card-title">{existing ? "Edit feed" : "New feed"}</h3>
      </div>
      {error && <div className="settings-error">{error}</div>}
      <SettingRow
        label="Name"
        htmlFor={`${field}-name`}
        help={
          existing
            ? "Repositories select a feed by its name, so it cannot change."
            : "How repositories pick this feed, e.g. company."
        }
      >
        <input
          id={`${field}-name`}
          type="text"
          className="settings-input"
          value={name}
          disabled={existing !== null}
          onChange={(e) => setName(e.target.value)}
          placeholder="company"
        />
      </SettingRow>
      <SettingRow
        label="Feed URL"
        htmlFor={`${field}-url`}
        help="https://pkgs.dev.azure.com/{organization}/_packaging/{feed} or https://{organization}.pkgs.visualstudio.com/_packaging/{feed}, with /{project} before /_packaging for a project-scoped feed."
      >
        <input
          id={`${field}-url`}
          type="text"
          className="settings-input feed-url-input"
          value={feedUrl}
          onChange={(e) => setFeedUrl(e.target.value)}
          placeholder="https://pkgs.dev.azure.com/example-org/_packaging/company"
        />
      </SettingRow>
      <SettingRow
        label="PAT"
        htmlFor={`${field}-pat`}
        help="Create a personal access token with only the Packaging (Read) scope, for this one organization, with a short expiry. It is stored encrypted and cannot be shown again."
      >
        <input
          id={`${field}-pat`}
          type="password"
          className="settings-input"
          autoComplete="off"
          value={pat}
          onChange={(e) => setPat(e.target.value)}
          placeholder={existing ? "(unchanged)" : ""}
        />
      </SettingRow>
      <div className="feed-form-actions">
        <button type="button" className="btn btn-secondary" onClick={onCancel}>
          Cancel
        </button>
        <button
          type="button"
          className="btn btn-primary"
          onClick={() => void save()}
          disabled={busy}
        >
          Save
        </button>
      </div>
    </section>
  );
}

interface FeedRowProps {
  feed: PackageFeed;
  onEdit: () => void;
  onDelete: () => Promise<void>;
}

function FeedRow({ feed, onEdit, onDelete }: FeedRowProps) {
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const remove = async () => {
    setDeleting(true);
    setError(null);
    try {
      await onDelete();
    } catch (err) {
      setError(describeError(err, "Failed to delete the feed."));
      setDeleting(false);
    }
  };

  return (
    <tr>
      <td>{feed.name}</td>
      <td className="feed-url">{feed.feedUrl}</td>
      <td className="feed-hint">{feed.patHint}</td>
      <td>
        {/* A result describes the feed as it was tested; a save starts afresh. */}
        <ConnectionTest
          key={`${feed.id}:${feed.updatedAt}`}
          run={() => packageFeedService.test(feed.id)}
        />
        {error && <div className="settings-error">{error}</div>}
      </td>
      <td className="feed-actions">
        <button type="button" className="btn" onClick={onEdit}>
          Edit
        </button>
        <button type="button" className="btn" onClick={() => void remove()} disabled={deleting}>
          Delete
        </button>
      </td>
    </tr>
  );
}

/**
 * Settings → Package feeds: the private Azure Artifacts feeds whose read-only
 * PATs are handed to the package managers of the runs whose repository selects
 * them. A PAT goes in and never comes back out; the list shows a masked hint.
 */
export default function PackageFeedsSettings() {
  const [feeds, setFeeds] = useState<PackageFeed[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [editing, setEditing] = useState<Editing | null>(null);
  // Only the newest list read may land, so a slow one cannot put back a feed a
  // later save or delete has already changed.
  const latestLoad = useRef(0);
  const formSeq = useRef(0);

  const open = (form: Form) => setEditing({ ...form, key: ++formSeq.current });

  const refresh = useCallback(async () => {
    const load = ++latestLoad.current;
    try {
      const loaded = await packageFeedService.list();
      if (load !== latestLoad.current) return;
      setLoadError(null);
      setFeeds(loaded);
    } catch (err) {
      if (load === latestLoad.current)
        setLoadError(describeError(err, "Failed to load the package feeds."));
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  // A form that finishes saving closes itself, and only itself: another opened
  // meanwhile stays open.
  const saved = (form: Editing) => async () => {
    await refresh();
    setEditing((current) => (current === form ? null : current));
  };

  const remove = async (feed: PackageFeed) => {
    await packageFeedService.remove(feed.id);
    setEditing((current) =>
      current?.kind === "edit" && current.feed.id === feed.id ? null : current,
    );
    await refresh();
  };

  return (
    <>
      <div className="settings-pane-header">
        <h2>Package feeds</h2>
        <p>
          Private npm and NuGet feeds (Azure Artifacts). A repository that selects a feed gets its
          credentials in every process of its runs and previews; its own <code>.npmrc</code> and{" "}
          <code>nuget.config</code> still decide where packages come from.
        </p>
      </div>

      <section className="settings-card">
        <div className="settings-card-header">
          <h3 className="settings-card-title">Feeds</h3>
          {editing === null && (
            <button type="button" className="btn btn-primary" onClick={() => open({ kind: "new" })}>
              Add feed
            </button>
          )}
        </div>
        {loadError && <div className="settings-error">{loadError}</div>}
        {feeds.length === 0 ? (
          <p className="settings-card-note">No feeds yet.</p>
        ) : (
          <table className="feed-table" aria-label="Package feeds">
            <thead>
              <tr>
                <th>Name</th>
                <th>Feed URL</th>
                <th>PAT</th>
                <th>Connection</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {feeds.map((feed) => (
                <FeedRow
                  key={feed.id}
                  feed={feed}
                  onEdit={() => open({ kind: "edit", feed })}
                  onDelete={() => remove(feed)}
                />
              ))}
            </tbody>
          </table>
        )}
      </section>

      {editing && (
        <FeedForm
          key={editing.key}
          form={editing}
          onSaved={saved(editing)}
          onCancel={() => setEditing(null)}
        />
      )}

      <style>{`
        .feed-table { width: 100%; border-collapse: collapse; font-size: 0.8rem; }
        .feed-table th {
          text-align: left;
          font-weight: 500;
          color: #707090;
          font-size: 0.7rem;
          text-transform: uppercase;
          letter-spacing: 0.05em;
          padding: 0 0.4rem 0.35rem;
        }
        .feed-table td { padding: 0.45rem 0.4rem; border-top: 1px solid #2d2d44; color: #c0c0d0; vertical-align: top; }
        .feed-url { font-family: monospace; word-break: break-all; }
        .feed-hint { font-family: monospace; white-space: nowrap; }
        .feed-actions { text-align: right; white-space: nowrap; }
        .feed-actions .btn { padding: 0.25rem 0.6rem; font-size: 0.75rem; background-color: #2d2d44; color: #c0c0d0; margin-left: 0.35rem; }
        .feed-actions .btn:hover { background-color: #3a3a5c; }
        .feed-url-input { width: 28rem; max-width: 100%; }
        .feed-form-actions { display: flex; justify-content: flex-end; gap: 0.5rem; margin-top: 0.75rem; }
      `}</style>
    </>
  );
}
