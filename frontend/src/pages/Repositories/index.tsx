import { useState, useEffect } from "react";
import {
  ApiError,
  PackageFeed,
  Repository,
  RepositoryInput,
  RemoteProvider,
  WorkItemStatus,
} from "../../types";
import { packageFeedService, repositoryService, remoteProviderService } from "../../services/auth";
import { useStoredPreviewEnv } from "../../hooks/useStoredPreviewEnv";
import ConnectionTest from "../../components/ConnectionTest";
import ProviderDialog from "./ProviderDialog";
import PreviewDataReset from "./PreviewDataReset";

export default function Repositories() {
  const [repositories, setRepositories] = useState<Repository[]>([]);
  const [providers, setProviders] = useState<RemoteProvider[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [retrying, setRetrying] = useState(false);
  const [showModal, setShowModal] = useState(false);
  const [editingRepo, setEditingRepo] = useState<Repository | null>(null);
  const [name, setName] = useState("");
  const [cloneUrl, setCloneUrl] = useState("");
  const [remoteProviderId, setRemoteProviderId] = useState("");
  const [defaultBranch, setDefaultBranch] = useState("main");
  const [worktreesPath, setWorktreesPath] = useState("");
  const [defaultIntakeStatus, setDefaultIntakeStatus] = useState<WorkItemStatus>(
    WorkItemStatus.Backlog,
  );
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);
  const [providerDialog, setProviderDialog] = useState<{ provider: RemoteProvider | null } | null>(
    null,
  );
  const [isInspecting, setIsInspecting] = useState(false);
  const [packageFeeds, setPackageFeeds] = useState<PackageFeed[]>([]);
  const [packageFeedsError, setPackageFeedsError] = useState<string | null>(null);
  const [selectedFeeds, setSelectedFeeds] = useState<string[]>([]);
  const [keepPreviewData, setKeepPreviewData] = useState(false);
  // The .env is masked out of the repository list, so the plaintext is fetched only
  // once an edit is open — never behind the page load — and only for the repository
  // being edited: a response for a previously-edited one is dropped.
  const previewEnv = useStoredPreviewEnv(editingRepo?.id ?? null);

  useEffect(() => {
    void loadData();
  }, []);

  useEffect(() => {
    packageFeedService
      .list()
      .then(setPackageFeeds)
      .catch(() => setPackageFeedsError("Could not load the package feeds."));
  }, []);

  const loadData = async () => {
    try {
      const [repos, provs] = await Promise.all([
        repositoryService.getEvery(),
        remoteProviderService.getEvery(),
      ]);
      setRepositories(repos);
      setProviders(provs);
      setLoadError(null);
    } catch (error) {
      setLoadError((error as ApiError | Error).message);
    } finally {
      setIsLoading(false);
    }
  };

  const openEdit = (repo: Repository) => {
    setShowModal(true);
    setEditingRepo(repo);
    setName(repo.name);
    setCloneUrl(repo.cloneUrl);
    setRemoteProviderId(repo.remoteProviderId);
    setDefaultBranch(repo.defaultBranch || "main");
    setWorktreesPath(repo.worktreesPath || "");
    setDefaultIntakeStatus(repo.defaultIntakeStatus);
    setSelectedFeeds(repo.packageFeeds?.map((f) => f.name) ?? []);
    setKeepPreviewData(repo.keepPreviewData ?? false);
    // Every form field starts from this repository, the .env included: a session
    // that was abandoned rather than saved must not carry into the new one.
    previewEnv.reset();
  };

  const openCreate = (providerId: string) => {
    setShowModal(true);
    setEditingRepo(null);
    setName("");
    setCloneUrl("");
    setRemoteProviderId(providerId);
    setDefaultBranch("main");
    setWorktreesPath("");
    setDefaultIntakeStatus(WorkItemStatus.Backlog);
    setSelectedFeeds([]);
    setKeepPreviewData(false);
    previewEnv.reset();
  };

  // When adding a repo, pull the name and default branch from the remote so the
  // user doesn't type them by hand and the stored default branch the diff relies
  // on is correct. Both stay editable; an unfetchable remote is a silent no-op.
  const inspectRemote = async () => {
    if (editingRepo || !cloneUrl.trim()) return;
    setIsInspecting(true);
    try {
      const info = await repositoryService.inspectRemote(
        cloneUrl.trim(),
        remoteProviderId || undefined,
      );
      if (info.name && !name.trim()) setName(info.name);
      if (info.defaultBranch) setDefaultBranch(info.defaultBranch);
    } catch (error) {
      console.error("Failed to inspect remote:", error);
    } finally {
      setIsInspecting(false);
    }
  };

  const handleSave = async () => {
    const data: RepositoryInput = {
      name,
      cloneUrl,
      remoteProviderId,
      defaultBranch,
      worktreesPath: worktreesPath || null,
      defaultIntakeStatus,
      packageFeeds: selectedFeeds,
      keepPreviewData,
      // The .env rides along only when the user changed the prefilled text; emptying
      // it removes the stored value instead, which this write cannot express and the
      // commit below does.
      ...previewEnv.pendingWrite,
    };

    try {
      if (editingRepo) {
        await repositoryService.update(editingRepo.id, data);
      } else {
        await repositoryService.create(data);
      }
      await previewEnv.commit();
      await loadData();
      setShowModal(false);
      setEditingRepo(null);
    } catch (error) {
      console.error("Failed to save repository:", error);
    }
  };

  const handleDelete = async (id: string) => {
    try {
      await repositoryService.delete(id);
      await loadData();
    } catch (error) {
      console.error("Failed to delete repository:", error);
    }
    setConfirmDelete(null);
  };

  const providerIds = new Set(providers.map((p) => p.id));
  const reposByProvider = new Map<string, Repository[]>();
  const orphans: Repository[] = [];
  for (const repo of repositories) {
    if (!providerIds.has(repo.remoteProviderId)) {
      orphans.push(repo);
      continue;
    }
    const list = reposByProvider.get(repo.remoteProviderId);
    if (list) list.push(repo);
    else reposByProvider.set(repo.remoteProviderId, [repo]);
  }
  const reposOf = (providerId: string) => reposByProvider.get(providerId) ?? [];

  // Feed names are matched ignoring case, as the server does.
  const sameFeed = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

  // Every feed there is, plus any the repository selected that is not among them:
  // a feed deleted since stays listed (and checked) until the user drops it.
  const feedOptions = [
    ...packageFeeds.map((f) => ({ name: f.name, missing: false })),
    ...(editingRepo?.packageFeeds ?? []).filter(
      (s) => !packageFeeds.some((f) => sameFeed(f.name, s.name)),
    ),
  ];

  const toggleFeed = (name: string, checked: boolean) =>
    setSelectedFeeds((current) =>
      checked ? [...current, name] : current.filter((s) => !sameFeed(s, name)),
    );

  if (isLoading) {
    return (
      <div className="page-container">
        <p>Loading...</p>
      </div>
    );
  }

  const renderRepo = (repo: Repository) => (
    <ConnectionTest
      key={`${repo.id}|${repo.updatedAt ?? repo.createdAt}`}
      run={() => repositoryService.test(repo.id)}
    >
      {({ button, outcome }) => (
        <div className="repo-row">
          <div className="repo-row-line">
            <div className="repo-id">
              <span className="repo-name">{repo.name}</span>
              <span className="repo-url">{repo.cloneUrl}</span>
            </div>
            <span className={`repo-status-badge status-${repo.defaultIntakeStatus}`}>
              {repo.defaultIntakeStatus}
            </span>
            <div className="repo-actions">
              {button}
              <button className="btn btn-secondary btn-small" onClick={() => openEdit(repo)}>
                Edit
              </button>
              {confirmDelete === repo.id ? (
                <div className="delete-confirm">
                  <button
                    className="btn btn-danger btn-small"
                    onClick={() => handleDelete(repo.id)}
                  >
                    Confirm
                  </button>
                  <button className="btn btn-small" onClick={() => setConfirmDelete(null)}>
                    Cancel
                  </button>
                </div>
              ) : (
                <button
                  className="btn btn-danger btn-small"
                  onClick={() => setConfirmDelete(repo.id)}
                >
                  Delete
                </button>
              )}
            </div>
          </div>
          {outcome && <div className="repo-outcome">{outcome}</div>}
        </div>
      )}
    </ConnectionTest>
  );

  return (
    <div className="page-container">
      <div className="repo-header">
        <h1 className="page-title">Repositories</h1>
        {(providers.length > 0 || orphans.length > 0) && (
          <button className="btn btn-primary" onClick={() => setProviderDialog({ provider: null })}>
            + New provider
          </button>
        )}
      </div>

      {loadError !== null ? (
        <div className="repo-load-error" role="alert">
          <p>{`Couldn't load the providers and repositories: ${loadError}`}</p>
          <button
            className="btn btn-secondary"
            disabled={retrying}
            onClick={() => {
              setRetrying(true);
              void loadData().finally(() => setRetrying(false));
            }}
          >
            {retrying ? "Retrying…" : "Retry"}
          </button>
        </div>
      ) : providers.length === 0 && orphans.length === 0 ? (
        <div className="repo-empty-page">
          <p>Add a remote provider first — repositories are added under one.</p>
          <button className="btn btn-primary" onClick={() => setProviderDialog({ provider: null })}>
            + New provider
          </button>
        </div>
      ) : (
        <div className="provider-list">
          {providers.map((provider) => {
            const repos = reposOf(provider.id);
            return (
              <section key={provider.id} className="provider-card" aria-label={provider.name}>
                {/* Keyed by the provider's version apart from its repositories, so a save
                    clears its own test outcome only. See docs/adr/0021-client-state-scoped-to-its-entity.md */}
                <ConnectionTest
                  key={`${provider.id}|${provider.updatedAt ?? provider.createdAt}`}
                  run={() => remoteProviderService.test(provider.id)}
                >
                  {({ button, outcome }) => (
                    <div className="provider-details">
                      <div className="provider-card-header">
                        <div className="provider-name">
                          {provider.name}
                          <span className="provider-type">{provider.type}</span>
                        </div>
                        <div className="repo-actions">
                          {button}
                          <button
                            id={`provider-edit-${provider.id}`}
                            className="btn btn-secondary btn-small"
                            onClick={() => setProviderDialog({ provider })}
                          >
                            Edit
                          </button>
                        </div>
                      </div>
                      <div className="provider-facts">
                        <span>
                          <span className="repo-label">URL</span>
                          <span className="repo-value">{provider.baseUrl}</span>
                        </span>
                        <span>
                          <span className="repo-label">API key</span>
                          <span className={`repo-value${provider.apiKey ? " repo-masked" : ""}`}>
                            {provider.apiKey ? "••••••••" : "(not set)"}
                          </span>
                        </span>
                      </div>
                      {outcome && <div className="provider-outcome">{outcome}</div>}
                    </div>
                  )}
                </ConnectionTest>
                <div className="provider-repos-head">
                  <span>Repositories ({repos.length})</span>
                  <button
                    className="btn btn-primary btn-small"
                    onClick={() => openCreate(provider.id)}
                  >
                    + Add repository
                  </button>
                </div>
                {repos.length === 0 ? (
                  <div className="provider-repos-empty">No repositories yet.</div>
                ) : (
                  <div className="provider-repos">{repos.map(renderRepo)}</div>
                )}
              </section>
            );
          })}
          {orphans.length > 0 && (
            <section className="provider-card" aria-label="Provider not found">
              <div className="provider-card-header">
                <div className="provider-name">Provider not found</div>
              </div>
              <div className="provider-facts">
                These repositories name a remote provider that is not in the list.
              </div>
              <div className="provider-repos">{orphans.map(renderRepo)}</div>
            </section>
          )}
        </div>
      )}

      {providerDialog && (
        <ProviderDialog
          provider={providerDialog.provider}
          onClose={() => setProviderDialog(null)}
          onSaved={loadData}
        />
      )}

      {showModal && (
        <div
          className="modal-overlay"
          onMouseDown={() => {
            setShowModal(false);
            setEditingRepo(null);
          }}
        >
          <div className="modal-content" onMouseDown={(e) => e.stopPropagation()}>
            <div className="modal-header">
              <h2>{editingRepo ? "Edit Repository" : "New Repository"}</h2>
              <button
                className="btn-close"
                onClick={() => {
                  setShowModal(false);
                  setEditingRepo(null);
                }}
              >
                &times;
              </button>
            </div>
            <div className="modal-body">
              <div className="form-group">
                <label htmlFor="repoName">Name</label>
                <input
                  id="repoName"
                  type="text"
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  required
                />
              </div>
              <div className="form-group">
                <label htmlFor="repoCloneUrl">Clone URL</label>
                <input
                  id="repoCloneUrl"
                  type="text"
                  value={cloneUrl}
                  onChange={(e) => setCloneUrl(e.target.value)}
                  onBlur={() => void inspectRemote()}
                  required
                />
                {isInspecting && <span className="repo-hint">Fetching remote details…</span>}
              </div>
              <div className="form-group">
                <label htmlFor="repoProvider">Remote Provider</label>
                <select
                  id="repoProvider"
                  value={remoteProviderId}
                  onChange={(e) => setRemoteProviderId(e.target.value)}
                  required
                >
                  <option value="">Select provider...</option>
                  {providers.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.name} ({p.type})
                    </option>
                  ))}
                </select>
              </div>
              <div className="form-group">
                <label htmlFor="repoDefaultBranch">Default Branch</label>
                <input
                  id="repoDefaultBranch"
                  type="text"
                  value={defaultBranch}
                  onChange={(e) => setDefaultBranch(e.target.value)}
                />
              </div>
              <div className="form-group">
                <label htmlFor="repoWorktreesPath">Worktrees Path</label>
                <input
                  id="repoWorktreesPath"
                  type="text"
                  value={worktreesPath}
                  onChange={(e) => setWorktreesPath(e.target.value)}
                />
              </div>
              <div className="form-group">
                <label htmlFor="repoPreviewEnv">Custom .env</label>
                <textarea
                  id="repoPreviewEnv"
                  className="repo-env-textarea"
                  rows={5}
                  value={previewEnv.value}
                  disabled={previewEnv.loading}
                  onChange={(e) => previewEnv.setValue(e.target.value)}
                  placeholder={
                    previewEnv.loading
                      ? "Loading…"
                      : "KEY=value\n# injected into preview processes; uncommitted secrets go here"
                  }
                  spellCheck={false}
                />
                {previewEnv.loadError && (
                  // An unreadable value must not look like an absent one: without
                  // this the field is blank and enabled, exactly like "none stored".
                  <span className="repo-hint repo-hint-error" role="alert">
                    {previewEnv.loadError}
                  </span>
                )}
                <span className="repo-hint">
                  Stored encrypted in ILD and injected as environment variables into preview install
                  steps and services on start — never written to a file in the worktree, so it can't
                  be committed. These override the per-service ild.config.json env, so a name left
                  here wins over the committed config. Clearing the field removes the stored .env.
                </span>
              </div>
              <div className="form-group">
                <label className="repo-feed-option">
                  <input
                    type="checkbox"
                    checked={keepPreviewData}
                    onChange={(e) => setKeepPreviewData(e.target.checked)}
                  />
                  Keep preview data between runs
                </label>
                <span className="repo-hint">
                  The volumes in ild.config.json become one folder for every work item and run of
                  this repository, so their data carries over from one preview to the next. Only one
                  preview of the repository runs at a time while this is on. Turning it off leaves
                  the data in place.
                </span>
                {editingRepo && <PreviewDataReset key={editingRepo.id} repository={editingRepo} />}
              </div>
              <div className="form-group">
                <span className="repo-group-label" id="repoPackageFeeds">
                  Package feeds
                </span>
                <div className="repo-feed-list" role="group" aria-labelledby="repoPackageFeeds">
                  {feedOptions.map((option) => (
                    <label key={option.name} className="repo-feed-option">
                      <input
                        type="checkbox"
                        checked={selectedFeeds.some((s) => sameFeed(s, option.name))}
                        onChange={(e) => toggleFeed(option.name, e.target.checked)}
                      />
                      {option.missing ? `${option.name} (missing)` : option.name}
                    </label>
                  ))}
                </div>
                {packageFeedsError && (
                  <span className="repo-hint repo-hint-error" role="alert">
                    {packageFeedsError}
                  </span>
                )}
                <span className="repo-hint">
                  {feedOptions.length === 0 && !packageFeedsError
                    ? "No package feeds yet — add them under Settings → Package feeds. "
                    : ""}
                  The selected feeds&apos; credentials reach every process of this repository&apos;s
                  runs and previews; its own .npmrc and nuget.config still decide where packages
                  come from.
                </span>
              </div>
              <div className="form-group">
                <label htmlFor="repoGating">Default Intake Status</label>
                <select
                  id="repoGating"
                  value={defaultIntakeStatus}
                  onChange={(e) => setDefaultIntakeStatus(e.target.value as WorkItemStatus)}
                >
                  <option value={WorkItemStatus.Backlog}>Backlog</option>
                  <option value={WorkItemStatus.WorkQueue}>Work Queue</option>
                </select>
              </div>
            </div>
            <div className="modal-footer">
              <button
                className="btn btn-secondary"
                onClick={() => {
                  setShowModal(false);
                  setEditingRepo(null);
                }}
              >
                Cancel
              </button>
              <button className="btn btn-primary" onClick={handleSave}>
                {editingRepo ? "Update" : "Create"}
              </button>
            </div>
          </div>
        </div>
      )}

      <style>{`
        .repo-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          margin-bottom: 1.5rem;
        }

        .repo-header .page-title {
          margin-bottom: 0;
        }

        .provider-list {
          display: flex;
          flex-direction: column;
          gap: 1.5rem;
        }

        .provider-card {
          background-color: #1e1e30;
          border-radius: 0.5rem;
          border: 1px solid #2d2d44;
          overflow: hidden;
        }

        .provider-card-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          padding: 1rem 1.25rem;
          border-bottom: 1px solid #2d2d44;
        }

        .provider-name {
          display: flex;
          align-items: baseline;
          gap: 0.6rem;
          font-size: 0.95rem;
          font-weight: 600;
          color: #e0e0e0;
        }

        .provider-type {
          font-size: 0.7rem;
          font-weight: 600;
          color: #a0a0b0;
          background-color: #2d2d44;
          padding: 0.1rem 0.45rem;
          border-radius: 0.25rem;
        }

        .provider-facts {
          display: flex;
          gap: 2.5rem;
          padding: 0.9rem 1.25rem 1rem;
          font-size: 0.85rem;
          color: #a0a0b0;
        }

        .provider-outcome,
        .repo-outcome {
          display: flex;
          flex-direction: column;
          gap: 0.5rem;
          padding: 0 1.25rem 0.9rem;
        }

        .provider-repos-head {
          display: flex;
          justify-content: space-between;
          align-items: center;
          padding: 0.85rem 1.25rem;
          border-top: 1px solid #2d2d44;
          font-size: 0.8rem;
          font-weight: 700;
          color: #c0c0d0;
          text-transform: uppercase;
          letter-spacing: 0.05em;
        }

        .provider-repos {
          padding-bottom: 0.75rem;
        }

        .provider-repos-empty {
          padding: 1rem 1.25rem;
          border-top: 1px solid #25253a;
          font-size: 0.82rem;
          color: #707090;
        }

        .repo-row {
          border-top: 1px solid #25253a;
        }

        .repo-row-line {
          display: grid;
          grid-template-columns: 1fr auto auto;
          gap: 1.5rem;
          align-items: center;
          padding: 0.85rem 1.25rem;
        }

        .repo-id {
          display: flex;
          flex-direction: column;
          gap: 0.25rem;
          min-width: 0;
        }

        .repo-name {
          font-size: 1rem;
          font-weight: 600;
          color: #e8e8f0;
        }

        .repo-url {
          font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
          font-size: 0.75rem;
          color: #5c5c78;
          overflow: hidden;
          text-overflow: ellipsis;
          white-space: nowrap;
        }

        .repo-actions {
          display: flex;
          gap: 0.6rem;
          align-items: center;
        }

        .delete-confirm {
          display: flex;
          gap: 0.25rem;
        }

        .repo-reset,
        .repo-reset-confirm {
          display: flex;
          flex-wrap: wrap;
          align-items: center;
          gap: 0.5rem;
          margin-top: 0.25rem;
        }

        .repo-load-error,
        .repo-empty-page {
          background-color: #1e1e30;
          border: 1px dashed #3a3a5c;
          border-radius: 0.5rem;
          padding: 2rem;
          text-align: center;
          color: #a0a0b0;
          font-size: 0.9rem;
        }

        .repo-load-error p,
        .repo-empty-page p {
          margin: 0 0 1rem;
        }

        .repo-masked {
          letter-spacing: 0.125rem;
          user-select: none;
        }

        .rp-secret-input-row {
          display: flex;
          gap: 0.5rem;
          align-items: center;
        }

        .rp-secret-input-row input {
          flex: 1;
          min-width: 0;
        }

        .repo-label {
          color: #707090;
          margin-right: 0.5rem;
        }

        .repo-value {
          color: #c0c0d0;
        }

        .repo-env-textarea {
          width: 100%;
          font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
          font-size: 0.8rem;
          resize: vertical;
        }

        .repo-status-badge {
          padding: 0.125rem 0.5rem;
          border-radius: 0.25rem;
          font-size: 0.75rem;
          font-weight: 600;
        }

        .status-Backlog {
          background-color: #3a3a5c;
          color: #a0a0b0;
        }

        .status-WorkQueue {
          background-color: #1a3a2a;
          color: #22c55e;
        }

        .btn {
          padding: 0.5rem 1rem;
          border: none;
          border-radius: 0.375rem;
          cursor: pointer;
          font-size: 0.875rem;
        }

        .btn-primary {
          background-color: #6366f1;
          color: #fff;
        }

        .btn-secondary {
          background-color: #2d2d44;
          color: #a0a0b0;
        }

        .btn-danger {
          background-color: #5c1a1a;
          color: #ef4444;
        }

        .btn-small {
          padding: 0.35rem 0.7rem;
          font-size: 0.78rem;
        }

        .modal-overlay {
          position: fixed;
          top: 0;
          left: 0;
          right: 0;
          bottom: 0;
          background-color: rgba(0, 0, 0, 0.6);
          display: flex;
          align-items: center;
          justify-content: center;
          z-index: 1000;
        }

        .modal-content {
          background-color: #1e1e30;
          border-radius: 0.5rem;
          border: 1px solid #2d2d44;
          width: 100%;
          max-width: 480px;
          padding: 1rem;
        }

        .modal-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          margin-bottom: 1rem;
        }

        .modal-header h2 {
          font-size: 1rem;
          color: #e0e0e0;
          margin: 0;
        }

        .btn-close {
          background: none;
          border: none;
          color: #a0a0b0;
          font-size: 1.25rem;
          cursor: pointer;
        }

        .modal-body {
          display: flex;
          flex-direction: column;
          gap: 0.75rem;
          margin-bottom: 1rem;
        }

        .modal-body .form-group {
          display: flex;
          flex-direction: column;
          gap: 0.25rem;
        }

        .modal-body label,
        .repo-group-label {
          font-size: 0.75rem;
          color: #a0a0b0;
        }

        .repo-feed-list {
          display: flex;
          flex-wrap: wrap;
          gap: 0.25rem 1rem;
        }

        .modal-body .repo-feed-option {
          display: flex;
          align-items: center;
          gap: 0.35rem;
          font-size: 0.85rem;
          color: #e0e0e0;
        }

        .repo-hint {
          font-size: 0.7rem;
          color: #707090;
        }

        .repo-hint-error {
          color: #f87171;
        }

        .modal-body input,
        .modal-body select {
          padding: 0.5rem;
          background-color: #2a2a40;
          border: 1px solid #3a3a5c;
          border-radius: 0.375rem;
          color: #e0e0e0;
          font-size: 0.875rem;
        }

        .modal-body .repo-feed-option input {
          padding: 0;
        }

        .modal-footer {
          display: flex;
          justify-content: flex-end;
          gap: 0.5rem;
        }
      `}</style>
    </div>
  );
}
