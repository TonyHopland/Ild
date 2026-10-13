import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router";
import { ApiError, GlobalVariable, GlobalVariableProposal } from "../../types";
import {
  GlobalVariableSettingKeys,
  globalVariableService,
  settingsService,
} from "../../services/auth";
import { useSignalR } from "../../hooks/useSignalR";
import GlobalVariableProposalCard from "../../components/GlobalVariableProposalCard";
import { ROUTES } from "../../utils/constants";
import VariableDialog from "./VariableDialog";
import VariableRow from "./VariableRow";

/** The setting's default, used while it is being read or when it cannot be. */
const DEFAULT_LIMIT = 4000;

const sameName = (a: string, b: string) => a.toUpperCase() === b.toUpperCase();

/**
 * Where a suggestion came from, linked to the card it shows as there. A source
 * that is gone, or another user's chat, is named without a link.
 */
function SuggestionSource({ proposal }: { proposal: GlobalVariableProposal }) {
  const { source } = proposal;
  const link = `?globalProposal=${encodeURIComponent(proposal.id)}`;
  if (source.kind === "run" && source.workItemId)
    return (
      <Link to={`${ROUTES.TASKBOARD}/${encodeURIComponent(source.workItemId)}${link}`}>
        Suggested by a run of work item #{source.workItemId}
      </Link>
    );
  if (source.kind === "chat" && source.chatSessionId && source.linkable)
    return (
      <Link to={`${ROUTES.CHAT}/${encodeURIComponent(source.chatSessionId)}${link}`}>
        Suggested in a chat
      </Link>
    );
  if (source.kind === "chat") return <>Suggested in another user&apos;s chat</>;
  return <>Source deleted</>;
}

export default function GlobalVariables() {
  const [variables, setVariables] = useState<GlobalVariable[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [limit, setLimit] = useState(DEFAULT_LIMIT);
  const [dialog, setDialog] = useState<{ variable: GlobalVariable | null } | null>(null);
  const [proposals, setProposals] = useState<GlobalVariableProposal[]>([]);
  const [proposalsError, setProposalsError] = useState<string | null>(null);
  // Only the latest read fills the lists: a reload after a save must not be
  // overwritten by an earlier read that answers after it.
  const loadSeq = useRef(0);

  const load = useCallback(async () => {
    const seq = ++loadSeq.current;
    const [list, pending] = await Promise.allSettled([
      globalVariableService.getAll(),
      globalVariableService.listProposals({ status: "Pending" }),
    ]);
    if (seq !== loadSeq.current) return;
    if (list.status === "fulfilled") {
      setVariables(list.value);
      setLoadError(null);
    } else {
      setLoadError((list.reason as ApiError | Error).message);
    }
    if (pending.status === "fulfilled") {
      setProposals(pending.value);
      setProposalsError(null);
    } else {
      setProposalsError((pending.reason as ApiError | Error).message);
    }
    setIsLoading(false);
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  // A suggestion made or decided anywhere, or a variable written that made some
  // stale, is a hint to read both lists again. Read once more after every
  // (re)connect, since a hint sent while away is lost.
  const { connectionState, on, off, invoke } = useSignalR("/hubs/work-item");
  useEffect(() => {
    if (connectionState !== "connected") return;
    let cancelled = false;
    void (async () => {
      try {
        await invoke("SubscribeToWorkItems");
      } catch (err) {
        console.error(err);
        return;
      }
      if (!cancelled) void load();
    })();
    return () => {
      cancelled = true;
    };
  }, [connectionState, invoke, load]);

  useEffect(() => {
    const onChanged = () => void load();
    on("GlobalVariableProposalsChanged", onChanged);
    return () => off("GlobalVariableProposalsChanged", onChanged);
  }, [on, off, load]);

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

  const newVariableProposals = proposals.filter(
    (p) => !variables.some((v) => sameName(p.name, v.name)),
  );
  const suggestionCards = (list: GlobalVariableProposal[]) =>
    list.length > 0 && (
      <div className="gv-suggestions">
        {list.map((p) => (
          <GlobalVariableProposalCard
            key={p.id}
            proposal={p}
            onSettled={() => void load()}
            source={<SuggestionSource proposal={p} />}
          />
        ))}
      </div>
    );

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
        or empty. Edits apply to the next prompt that is rendered; there is no history. Agents
        cannot change them: they suggest a new value, which waits here for a human to approve.
      </p>

      {loadError && (
        <div className="gv-error" role="alert">
          {`Couldn't load the global variables: ${loadError}`}{" "}
          <button className="btn btn-secondary btn-small" onClick={() => void load()}>
            Retry
          </button>
        </div>
      )}
      {proposalsError && (
        <div className="gv-error" role="alert">
          {`Couldn't load the suggestions: ${proposalsError}`}{" "}
          <button className="btn btn-secondary btn-small" onClick={() => void load()}>
            Retry
          </button>
        </div>
      )}

      {variables.length === 0 && !loadError ? (
        <p className="gv-empty">No global variables yet.</p>
      ) : (
        <div className="gv-list">
          {variables.map((v) => (
            <div key={v.id} className="gv-entry">
              <VariableRow variable={v} onEdit={() => setDialog({ variable: v })} reload={load} />
              {suggestionCards(proposals.filter((p) => sameName(p.name, v.name)))}
            </div>
          ))}
        </div>
      )}

      {newVariableProposals.length > 0 && (
        <section className="gv-new" aria-label="New variables">
          <h2 className="gv-new-title">New variables</h2>
          {suggestionCards(newVariableProposals)}
        </section>
      )}

      {dialog && (
        <VariableDialog
          variable={dialog.variable}
          limit={limit}
          onClose={() => setDialog(null)}
          reload={load}
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

        .gv-suggestions {
          margin: 0.5rem 0 0 1.5rem;
        }

        .gv-new {
          margin-top: 1.5rem;
        }

        .gv-new-title {
          color: #e0e0e0;
          font-size: 1rem;
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
