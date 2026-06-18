import { WorkItem } from "../types";

interface CombinedPreviewDrawerProps {
  items: WorkItem[];
  onClose: () => void;
}

// The integration-branch name the backend will create to compose the selected
// runs. Mirrored here so the drawer can preview what *will* happen before any
// backend exists. Kept deterministic (sorted ids) so the same selection always
// shows the same name.
function integrationBranchName(items: WorkItem[]): string {
  const ids = items
    .map((i) => i.id)
    .sort()
    .join("-");
  return `ild/combined-${ids}`;
}

// Side drawer that completes the "preview together" flow visually. The backend
// service that merges the member branches into an integration worktree and
// starts a preview does not exist yet, so this lays out the plan — the member
// branches and the steps that will run — and is explicit about being a stub.
// Once the endpoint lands, the bottom section is replaced by the existing
// preview panel pointed at the integration worktree.
export default function CombinedPreviewDrawer({ items, onClose }: CombinedPreviewDrawerProps) {
  const branch = integrationBranchName(items);

  return (
    <div className="combined-preview-backdrop" onClick={onClose}>
      <aside
        className="combined-preview-drawer"
        role="dialog"
        aria-label="Combined preview"
        onClick={(e) => e.stopPropagation()}
      >
        <header className="cpd-header">
          <h2 className="cpd-title">Combined preview</h2>
          <button type="button" className="cpd-close" onClick={onClose} aria-label="Close">
            ×
          </button>
        </header>

        <section className="cpd-section">
          <div className="cpd-section-label">Integration branch</div>
          <code className="cpd-branch">{branch}</code>
          <p className="cpd-hint">
            Branched off <code>main</code>; the {items.length} member branches below are merged into
            it in order, then a preview is started from its worktree.
          </p>
        </section>

        <section className="cpd-section">
          <div className="cpd-section-label">Members ({items.length})</div>
          <ul className="cpd-members">
            {items.map((item) => (
              <li key={item.id} className="cpd-member">
                <span className="cpd-member-id">#{item.id}</span>
                <span className="cpd-member-title">{item.title}</span>
                <code className="cpd-member-branch">
                  {item.branchName ?? `ild/wi-${item.id}-run-…`}
                </code>
              </li>
            ))}
          </ul>
        </section>

        <section className="cpd-section cpd-stub">
          <div className="cpd-stub-badge">Not wired up yet</div>
          <p className="cpd-hint">
            Starting the preview needs the backend integration-branch service (merge N branches into
            one throwaway worktree, then run the existing preview). This drawer shows the plan; the
            preview panel will render here once that endpoint exists.
          </p>
        </section>

        <footer className="cpd-footer">
          <button type="button" className="cpd-btn" onClick={onClose}>
            Close
          </button>
          <button type="button" className="cpd-btn cpd-btn-primary" disabled>
            Start preview
          </button>
        </footer>

        <style>{`
          .combined-preview-backdrop {
            position: fixed;
            inset: 0;
            background-color: rgba(0, 0, 0, 0.5);
            display: flex;
            justify-content: flex-end;
            z-index: 50;
          }

          .combined-preview-drawer {
            width: min(420px, 100%);
            height: 100%;
            background-color: #1e1e30;
            border-left: 1px solid #3a3a5c;
            box-shadow: -8px 0 30px rgba(0, 0, 0, 0.4);
            display: flex;
            flex-direction: column;
            overflow-y: auto;
          }

          .cpd-header {
            display: flex;
            align-items: center;
            justify-content: space-between;
            padding: 0.9rem 1rem;
            border-bottom: 1px solid #2d2d44;
          }

          .cpd-title {
            font-size: 0.95rem;
            font-weight: 600;
            color: #e0e0e0;
            margin: 0;
          }

          .cpd-close {
            background: none;
            border: none;
            color: #a0a0b0;
            font-size: 1.4rem;
            line-height: 1;
            cursor: pointer;
          }
          .cpd-close:hover { color: #e0e0e0; }

          .cpd-section {
            padding: 0.9rem 1rem;
            border-bottom: 1px solid #2d2d44;
          }

          .cpd-section-label {
            font-size: 0.7rem;
            text-transform: uppercase;
            letter-spacing: 0.05em;
            color: #7f849c;
            margin-bottom: 0.5rem;
          }

          .cpd-branch,
          .cpd-member-branch {
            font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
            font-size: 0.74rem;
            color: #c7d2fe;
            background-color: #2a2a40;
            padding: 0.15rem 0.4rem;
            border-radius: 0.25rem;
          }

          .cpd-hint {
            font-size: 0.76rem;
            color: #a0a0b0;
            line-height: 1.5;
            margin: 0.5rem 0 0;
          }
          .cpd-hint code {
            font-size: 0.72rem;
            background-color: #2a2a40;
            padding: 0.05rem 0.3rem;
            border-radius: 0.2rem;
            color: #c0c0d0;
          }

          .cpd-members {
            list-style: none;
            margin: 0;
            padding: 0;
            display: flex;
            flex-direction: column;
            gap: 0.5rem;
          }

          .cpd-member {
            display: grid;
            grid-template-columns: auto 1fr;
            gap: 0.15rem 0.5rem;
            align-items: baseline;
            background-color: #23233b;
            border: 1px solid #3a3a5c;
            border-radius: 0.375rem;
            padding: 0.5rem 0.6rem;
          }

          .cpd-member-id {
            font-size: 0.72rem;
            color: #7f849c;
            letter-spacing: 0.04em;
          }

          .cpd-member-title {
            font-size: 0.82rem;
            color: #e0e0e0;
          }

          .cpd-member-branch {
            grid-column: 2;
          }

          .cpd-stub-badge {
            display: inline-block;
            font-size: 0.68rem;
            font-weight: 600;
            text-transform: uppercase;
            letter-spacing: 0.03em;
            color: #fbbf24;
            background-color: #2d2a1a;
            border: 1px solid #d97706;
            border-radius: 0.25rem;
            padding: 0.15rem 0.45rem;
          }

          .cpd-footer {
            margin-top: auto;
            display: flex;
            justify-content: flex-end;
            gap: 0.5rem;
            padding: 0.9rem 1rem;
            border-top: 1px solid #2d2d44;
          }

          .cpd-btn {
            padding: 0.45rem 0.85rem;
            border-radius: 0.375rem;
            border: 1px solid #3a3a5c;
            background-color: #2a2a40;
            color: #e0e0e0;
            font-size: 0.82rem;
            cursor: pointer;
          }
          .cpd-btn:hover { background-color: #32324c; }
          .cpd-btn-primary {
            background-color: #6366f1;
            border-color: #6366f1;
            color: #fff;
          }
          .cpd-btn-primary:disabled {
            opacity: 0.5;
            cursor: not-allowed;
          }
        `}</style>
      </aside>
    </div>
  );
}
