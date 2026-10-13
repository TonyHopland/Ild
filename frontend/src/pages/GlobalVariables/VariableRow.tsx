import { useEffect, useRef, useState } from "react";
import { Link } from "react-router";
import { ApiError, GlobalVariable } from "../../types";
import { globalVariableService } from "../../services/auth";

interface VariableRowProps {
  variable: GlobalVariable;
  onEdit: () => void;
  /** Reads the list again; called after every delete attempt, since a refused one may mean the row is stale. */
  reload: () => Promise<void>;
}

/**
 * One global variable. The delete confirmation, its in-flight flag and its
 * error are this row's own, so a delete running on one row never touches another.
 */
export default function VariableRow({ variable: v, onEdit, reload }: VariableRowProps) {
  const [confirming, setConfirming] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const deleteRef = useRef<HTMLButtonElement>(null);
  const refocusDelete = useRef(false);

  useEffect(() => {
    if (confirming || !refocusDelete.current) return;
    refocusDelete.current = false;
    deleteRef.current?.focus();
  }, [confirming]);

  const cancel = () => {
    refocusDelete.current = true;
    setConfirming(false);
  };

  const handleDelete = async () => {
    setDeleting(true);
    setError(null);
    try {
      await globalVariableService.delete(v.id);
    } catch (err) {
      setError((err as ApiError | Error).message);
      cancel();
    } finally {
      setDeleting(false);
    }
    await reload();
  };

  return (
    <section className="gv-row" aria-label={v.name}>
      <div className="gv-row-main">
        <div className="gv-row-head">
          <code className="gv-name">{v.name}</code>
          <span className="gv-updated">Updated {new Date(v.updatedAt).toLocaleString()}</span>
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
        {error && (
          <div className="gv-error" role="alert">
            {`Couldn't delete the variable: ${error}`}
          </div>
        )}
      </div>
      <div className="gv-actions">
        <button className="btn btn-secondary btn-small" onClick={onEdit}>
          Edit
        </button>
        {confirming ? (
          <>
            <button
              className="btn btn-danger btn-small"
              autoFocus
              disabled={deleting}
              onClick={() => void handleDelete()}
            >
              Confirm delete
            </button>
            <button className="btn btn-secondary btn-small" disabled={deleting} onClick={cancel}>
              Cancel
            </button>
          </>
        ) : (
          <button
            ref={deleteRef}
            className="btn btn-danger btn-small"
            onClick={() => setConfirming(true)}
          >
            Delete
          </button>
        )}
      </div>
    </section>
  );
}
