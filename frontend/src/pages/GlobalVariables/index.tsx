import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router";
import { ApiError, GlobalVariable } from "../../types";
import {
  GlobalVariableSettingKeys,
  globalVariableService,
  settingsService,
} from "../../services/auth";
import VariableDialog from "./VariableDialog";

/** The setting's default, used while it is being read or when it cannot be. */
const DEFAULT_LIMIT = 4000;

export default function GlobalVariables() {
  const [variables, setVariables] = useState<GlobalVariable[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [limit, setLimit] = useState(DEFAULT_LIMIT);
  const [dialog, setDialog] = useState<{ variable: GlobalVariable | null } | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<string | null>(null);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  // Only the latest read fills the list: a reload after a save must not be
  // overwritten by an earlier read that answers after it.
  const loadSeq = useRef(0);

  const load = useCallback(async () => {
    const seq = ++loadSeq.current;
    try {
      const list = await globalVariableService.getAll();
      if (seq !== loadSeq.current) return;
      setVariables(list);
      setLoadError(null);
    } catch (error) {
      if (seq !== loadSeq.current) return;
      setLoadError((error as ApiError | Error).message);
    } finally {
      if (seq === loadSeq.current) setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    let active = true;
    settingsService
      .get(GlobalVariableSettingKeys.MaxLength)
      .then((s) => {
        const n = parseInt(s.value, 10);
        if (active && !Number.isNaN(n)) setLimit(n);
      })
      // The server enforces the limit either way; the counter falls back to the default.
      .catch(() => {});
    return () => {
      active = false;
    };
  }, []);

  const handleDelete = async (id: string) => {
    setDeleting(id);
    setDeleteError(null);
    try {
      await globalVariableService.delete(id);
      setConfirmDelete(null);
      await load();
    } catch (error) {
      setDeleteError((error as ApiError | Error).message);
    } finally {
      setDeleting(null);
    }
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
      <div className="gv-header">
        <h1 className="page-title">Global Variables</h1>
        <button className="btn btn-primary" onClick={() => setDialog({ variable: null })}>
          New variable
        </button>
      </div>
      <p className="gv-intro">
        Values every loop prompt can read with <code>{"{{Global.<name>}}"}</code>, for conventions
        that apply across all repositories. A run halts before a node that uses one that is missing
        or empty. Edits apply to the next prompt that is rendered; there is no history.
      </p>

      {loadError && (
        <div className="gv-error" role="alert">
          {`Couldn't load the global variables: ${loadError}`}{" "}
          <button className="btn btn-secondary btn-small" onClick={() => void load()}>
            Retry
          </button>
        </div>
      )}
      {deleteError && (
        <div className="gv-error" role="alert">
          {`Couldn't delete the variable: ${deleteError}`}
        </div>
      )}

      {variables.length === 0 && !loadError ? (
        <p className="gv-empty">No global variables yet.</p>
      ) : (
        <div className="gv-list">
          {variables.map((v) => (
            <section key={v.id} className="gv-row" aria-label={v.name}>
              <div className="gv-row-main">
                <div className="gv-row-head">
                  <code className="gv-name">{v.name}</code>
                  <span className="gv-updated">
                    Updated {new Date(v.updatedAt).toLocaleString()}
                  </span>
                </div>
                <pre className="gv-value">{v.value === "" ? <em>(empty)</em> : v.value}</pre>
                <div className="gv-used-by">
                  {v.usedBy.length === 0 ? (
                    "Not used by any loop template."
                  ) : (
                    <>
                      Used by{" "}
                      {v.usedBy.map((t, i) => (
                        <span key={t.id}>
                          {i > 0 && ", "}
                          <Link to={`/loop-editor/${t.id}`}>{t.name}</Link>
                        </span>
                      ))}
                    </>
                  )}
                </div>
              </div>
              <div className="gv-actions">
                <button
                  className="btn btn-secondary btn-small"
                  onClick={() => setDialog({ variable: v })}
                >
                  Edit
                </button>
                {confirmDelete === v.id ? (
                  <>
                    <button
                      className="btn btn-danger btn-small"
                      disabled={deleting === v.id}
                      onClick={() => void handleDelete(v.id)}
                    >
                      Confirm delete
                    </button>
                    <button
                      className="btn btn-secondary btn-small"
                      disabled={deleting === v.id}
                      onClick={() => setConfirmDelete(null)}
                    >
                      Cancel
                    </button>
                  </>
                ) : (
                  <button
                    className="btn btn-danger btn-small"
                    onClick={() => setConfirmDelete(v.id)}
                  >
                    Delete
                  </button>
                )}
              </div>
            </section>
          ))}
        </div>
      )}

      {dialog && (
        <VariableDialog
          variable={dialog.variable}
          limit={limit}
          onClose={() => setDialog(null)}
          onSaved={load}
        />
      )}

      <style>{`
        .gv-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
        }

        .gv-intro,
        .gv-empty {
          color: #a0a0b0;
          font-size: 0.875rem;
        }

        .gv-error {
          color: #f87171;
          font-size: 0.875rem;
          margin: 0.5rem 0;
        }

        .gv-list {
          display: flex;
          flex-direction: column;
          gap: 0.75rem;
        }

        .gv-row {
          display: flex;
          align-items: flex-start;
          gap: 0.75rem;
          background-color: #1e1e30;
          border: 1px solid #2d2d44;
          border-radius: 0.5rem;
          padding: 0.75rem 1rem;
        }

        .gv-row-main {
          flex: 1;
          min-width: 0;
        }

        .gv-row-head {
          display: flex;
          align-items: center;
          gap: 0.75rem;
        }

        .gv-name {
          color: #e0e0e0;
          font-weight: 600;
        }

        .gv-updated {
          color: #a0a0b0;
          font-size: 0.75rem;
        }

        .gv-actions {
          display: flex;
          gap: 0.5rem;
        }

        .gv-value {
          white-space: pre-wrap;
          word-break: break-word;
          color: #d0d0e0;
          font-size: 0.85rem;
          margin: 0.5rem 0;
        }

        .gv-used-by {
          color: #a0a0b0;
          font-size: 0.8rem;
        }

        .gv-used-by a {
          color: #a5b4fc;
        }

        .btn-danger {
          background-color: #7f1d1d;
          color: #fecaca;
        }

        .btn-small {
          padding: 0.35rem 0.7rem;
          font-size: 0.78rem;
        }

        .btn-close {
          background: none;
          border: none;
          color: #a0a0b0;
          font-size: 1.25rem;
          cursor: pointer;
        }

        .gv-value-input {
          font-family: inherit;
          resize: vertical;
        }
      `}</style>
    </div>
  );
}
