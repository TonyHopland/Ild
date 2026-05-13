import { useState, useEffect } from "react";
import { Repository, RemoteProvider, WorkItemStatus } from "../../types";
import { repositoryService, remoteProviderService } from "../../services/auth";

export default function Repositories() {
  const [repositories, setRepositories] = useState<Repository[]>([]);
  const [providers, setProviders] = useState<RemoteProvider[]>([]);
  const [isLoading, setIsLoading] = useState(true);
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

  useEffect(() => {
    void loadData();
  }, []);

  const loadData = async () => {
    try {
      const [repos, provs] = await Promise.all([
        repositoryService.getAll(),
        remoteProviderService.getAll(),
      ]);
      setRepositories(repos);
      setProviders(provs);
    } catch (error) {
      console.error("Failed to load repositories:", error);
    } finally {
      setIsLoading(false);
    }
  };

  const openEdit = (repo: Repository) => {
    setEditingRepo(repo);
    setName(repo.name);
    setCloneUrl(repo.cloneUrl);
    setRemoteProviderId(repo.remoteProviderId);
    setDefaultBranch(repo.defaultBranch || "main");
    setWorktreesPath(repo.worktreesPath || "");
    setDefaultIntakeStatus(repo.defaultIntakeStatus);
  };

  const openCreate = () => {
    setShowModal(true);
    setEditingRepo(null);
    setName("");
    setCloneUrl("");
    setRemoteProviderId(providers[0]?.id || "");
    setDefaultBranch("main");
    setWorktreesPath("");
    setDefaultIntakeStatus(WorkItemStatus.Backlog);
  };

  const handleSave = async () => {
    const data: Partial<Repository> = {
      name,
      cloneUrl,
      remoteProviderId,
      defaultBranch,
      worktreesPath: worktreesPath || null,
      defaultIntakeStatus,
    };

    try {
      if (editingRepo) {
        await repositoryService.update(editingRepo.id, data);
      } else {
        await repositoryService.create(data);
      }
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

  const getProviderName = (providerId: string) => {
    const provider = providers.find((p) => p.id === providerId);
    return provider?.name || providerId;
  };

  if (isLoading) {
    return (
      <div className="page-container">
        <p>Loading...</p>
      </div>
    );
  }

  return (
    <div className="page-container">
      <div className="repo-header">
        <h1 className="page-title">Repositories</h1>
        <button className="btn btn-primary" onClick={openCreate}>
          + New Repository
        </button>
      </div>

      <div className="repo-list">
        {repositories.map((repo) => (
          <div key={repo.id} className="repo-card">
            <div className="repo-card-header">
              <div className="repo-name">{repo.name}</div>
              <div className="repo-actions">
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
            <div className="repo-card-body">
              <div className="repo-field">
                <span className="repo-label">Clone URL</span>
                <span className="repo-value">{repo.cloneUrl}</span>
              </div>
              <div className="repo-field">
                <span className="repo-label">Provider</span>
                <span className="repo-value">{getProviderName(repo.remoteProviderId)}</span>
              </div>
              <div className="repo-field">
                <span className="repo-label">Gating</span>
                <span className={`repo-value repo-status-badge status-${repo.defaultIntakeStatus}`}>
                  {repo.defaultIntakeStatus}
                </span>
              </div>
            </div>
          </div>
        ))}
      </div>

      {(showModal || confirmDelete !== null) && (
        <div
          className="modal-overlay"
          onClick={() => {
            setShowModal(false);
            setEditingRepo(null);
            setConfirmDelete(null);
          }}
        >
          <div className="modal-content" onClick={(e) => e.stopPropagation()}>
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
                  required
                />
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
          margin-bottom: 1rem;
        }

        .repo-list {
          display: flex;
          flex-direction: column;
          gap: 0.75rem;
        }

        .repo-card {
          background-color: #1e1e30;
          border-radius: 0.5rem;
          border: 1px solid #2d2d44;
          overflow: hidden;
        }

        .repo-card-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          padding: 0.75rem 1rem;
          border-bottom: 1px solid #2d2d44;
        }

        .repo-name {
          font-size: 0.9rem;
          font-weight: 600;
          color: #e0e0e0;
        }

        .repo-actions {
          display: flex;
          gap: 0.5rem;
          align-items: center;
        }

        .delete-confirm {
          display: flex;
          gap: 0.25rem;
        }

        .repo-card-body {
          padding: 0.75rem 1rem;
          display: flex;
          flex-direction: column;
          gap: 0.5rem;
        }

        .repo-field {
          display: flex;
          gap: 0.5rem;
          font-size: 0.8rem;
        }

        .repo-label {
          color: #707090;
          min-width: 8rem;
        }

        .repo-value {
          color: #c0c0d0;
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
          padding: 0.25rem 0.5rem;
          font-size: 0.75rem;
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

        .modal-body label {
          font-size: 0.75rem;
          color: #a0a0b0;
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

        .modal-footer {
          display: flex;
          justify-content: flex-end;
          gap: 0.5rem;
        }
      `}</style>
    </div>
  );
}
