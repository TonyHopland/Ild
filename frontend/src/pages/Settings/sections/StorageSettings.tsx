import { useCallback, useEffect, useId, useRef, useState } from "react";
import { storageService } from "../../../services/auth";
import type {
  ApiError,
  StorageOverview,
  WorktreeCleanupCandidate,
  WorktreeCleanupResult,
} from "../../../types";
import { formatBytes } from "../../../utils/attachments";

type Overview = { data: StorageOverview | null; loading: boolean; error: string | null };

type Cleanup =
  | { kind: "idle" }
  | { kind: "loadingCandidates" }
  | { kind: "nothing" }
  | { kind: "confirming"; candidates: WorktreeCleanupCandidate[]; totalBytes: number | null }
  | { kind: "cleaning"; candidates: WorktreeCleanupCandidate[]; totalBytes: number | null }
  | { kind: "done"; candidates: WorktreeCleanupCandidate[]; result: WorktreeCleanupResult }
  | { kind: "error"; message: string };

const failure = (error: unknown) => (error as ApiError | Error | undefined)?.message || null;

function Size({ bytes, incomplete }: { bytes: number | null; incomplete: boolean }) {
  if (bytes === null)
    return <span className="storage-size storage-not-measured">not measured</span>;
  return (
    <span className="storage-size">
      {formatBytes(bytes)}
      {incomplete && (
        <span
          className="storage-incomplete"
          title="Some of it could not be read, so the real figure may be higher."
        >
          {" "}
          (incomplete)
        </span>
      )}
    </span>
  );
}

/** A label and its size. The spaces keep the two apart in the text a screen reader or copy-paste gets. */
function Line({
  label,
  bytes,
  incomplete,
}: {
  label: string;
  bytes: number | null;
  incomplete: boolean;
}) {
  return (
    <div className="storage-line">
      <span className="storage-label">{label}</span>{" "}
      <Size bytes={bytes} incomplete={incomplete} />{" "}
    </div>
  );
}

function repositoryOf(candidate: WorktreeCleanupCandidate) {
  if (candidate.repositoryName) return candidate.repositoryName;
  return candidate.repositoryId ? "unknown repository" : "no repository";
}

/** How much disk ILD takes, and the cleanup of worktrees whose work items are Done. */
export default function StorageSettings() {
  const [overview, setOverview] = useState<Overview>({ data: null, loading: true, error: null });
  const [cleanup, setCleanup] = useState<Cleanup>({ kind: "idle" });
  const mounted = useRef(true);
  // Only the latest measurement may land: an earlier one finishing last would show older figures.
  const latestMeasurement = useRef(0);
  // A measurement walks the server's disk; one nobody will read is aborted, which cancels the walk.
  const measurementAbort = useRef<AbortController | null>(null);
  const confirmPanel = useRef<HTMLDivElement>(null);
  const cleanupButton = useRef<HTMLButtonElement>(null);
  // Set when the control that had focus goes away, so it lands back on the cleanup button.
  const returnFocus = useRef(false);
  const listHeading = useId();

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      measurementAbort.current?.abort();
    };
  }, []);

  useEffect(() => {
    if (cleanup.kind === "confirming") {
      confirmPanel.current?.focus();
    } else if (returnFocus.current && cleanupButton.current) {
      returnFocus.current = false;
      cleanupButton.current.focus();
    }
  }, [cleanup.kind]);

  const measure = useCallback(async () => {
    const request = ++latestMeasurement.current;
    const current = () => mounted.current && request === latestMeasurement.current;
    measurementAbort.current?.abort();
    const abort = new AbortController();
    measurementAbort.current = abort;
    setOverview((previous) => ({ ...previous, loading: true, error: null }));
    try {
      const data = await storageService.getOverview(abort.signal);
      if (current()) setOverview({ data, loading: false, error: null });
    } catch (error) {
      if (current())
        setOverview({
          data: null,
          loading: false,
          error: failure(error) ?? "Could not measure storage.",
        });
    }
  }, []);

  useEffect(() => {
    void measure();
  }, [measure]);

  const listCandidates = async () => {
    setCleanup({ kind: "loadingCandidates" });
    try {
      const { candidates, totalBytes } = await storageService.getCleanupCandidates();
      if (!mounted.current) return;
      setCleanup(
        candidates.length === 0
          ? { kind: "nothing" }
          : { kind: "confirming", candidates, totalBytes },
      );
    } catch (error) {
      if (mounted.current)
        setCleanup({
          kind: "error",
          message: failure(error) ?? "Could not list the worktrees to clean up.",
        });
    }
  };

  const confirm = async (candidates: WorktreeCleanupCandidate[], totalBytes: number | null) => {
    setCleanup({ kind: "cleaning", candidates, totalBytes });
    try {
      const result = await storageService.cleanUpWorktrees(candidates.map((c) => c.runId));
      returnFocus.current = true;
      if (mounted.current) setCleanup({ kind: "done", candidates, result });
    } catch (error) {
      returnFocus.current = true;
      if (mounted.current)
        setCleanup({
          kind: "error",
          message: failure(error) ?? "The cleanup request failed.",
        });
    }
    if (mounted.current) void measure();
  };

  const cleanupBusy = cleanup.kind === "loadingCandidates" || cleanup.kind === "cleaning";

  return (
    <>
      <div className="settings-pane-header">
        <h2>Storage</h2>
        <p>
          How much disk space ILD's data takes. Each file is counted once, by the space it actually
          occupies, however many hard links it has.
        </p>
      </div>

      <section className="settings-card">
        <div className="settings-card-header">
          <h3 className="settings-card-title">Disk usage</h3>
          <button
            type="button"
            className="btn btn-secondary"
            onClick={() => void measure()}
            disabled={overview.loading || cleanupBusy}
          >
            Refresh
          </button>
        </div>
        {overview.loading && (
          <p className="settings-card-note" role="status">
            Measuring...
          </p>
        )}
        {overview.error && (
          <div className="settings-error" role="alert">
            Could not measure storage: {overview.error}
          </div>
        )}
        {overview.data && (
          <ul className="storage-categories">
            {overview.data.categories.map((category) => (
              <li key={category.key} className="storage-category">
                <Line
                  label={category.label}
                  bytes={category.bytes}
                  incomplete={category.incomplete}
                />
                {category.entries.length > 0 && (
                  <ul className="storage-entries">
                    {category.entries.map((entry) => (
                      <li key={entry.key}>
                        <Line
                          label={entry.label}
                          bytes={entry.bytes}
                          incomplete={entry.incomplete}
                        />
                      </li>
                    ))}
                  </ul>
                )}
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="settings-card">
        <div className="settings-card-header">
          <h3 className="settings-card-title">Worktrees of Done work items</h3>
        </div>
        <p className="settings-card-note">
          Removes the worktree and local branch of every finished, unpinned run whose work item is
          Done and has no active run. The runs and their history stay; remote branches and pull
          requests are not touched.
        </p>

        {cleanup.kind === "confirming" || cleanup.kind === "cleaning" ? (
          <div
            className="storage-cleanup-confirm"
            ref={confirmPanel}
            tabIndex={-1}
            role="group"
            aria-labelledby={listHeading}
          >
            <p id={listHeading}>These worktrees will be removed:</p>
            <ul className="storage-entries">
              {cleanup.candidates.map((candidate) => (
                <li key={candidate.runId}>
                  <Line
                    label={`${candidate.workItemTitle} (${repositoryOf(candidate)})`}
                    bytes={candidate.bytes}
                    incomplete={candidate.incomplete}
                  />
                </li>
              ))}
            </ul>
            <p>
              {cleanup.totalBytes === null ? (
                "Space freed: not measured."
              ) : (
                <>
                  Frees up to {formatBytes(cleanup.totalBytes)}
                  {cleanup.candidates.some((c) => c.incomplete) && " (incomplete)"}.
                </>
              )}
            </p>
            <div className="storage-actions">
              <button
                type="button"
                className="btn btn-danger"
                onClick={() => void confirm(cleanup.candidates, cleanup.totalBytes)}
                disabled={cleanupBusy}
              >
                Confirm cleanup
              </button>
              <button
                type="button"
                className="btn btn-secondary"
                onClick={() => {
                  returnFocus.current = true;
                  setCleanup({ kind: "idle" });
                }}
                disabled={cleanupBusy}
              >
                Cancel
              </button>
            </div>
          </div>
        ) : (
          <>
            {cleanup.kind === "nothing" && (
              <p role="status">Nothing to clean up: no worktree belongs to a Done work item.</p>
            )}
            {cleanup.kind === "error" && (
              <div className="settings-error" role="alert">
                {cleanup.message}
              </div>
            )}
            {cleanup.kind === "done" && (
              <CleanupSummary candidates={cleanup.candidates} result={cleanup.result} />
            )}
            <button
              type="button"
              className="btn btn-primary"
              ref={cleanupButton}
              onClick={() => void listCandidates()}
              disabled={cleanupBusy}
            >
              Clean up worktrees of Done work items
            </button>
          </>
        )}
      </section>
    </>
  );
}

function CleanupSummary({
  candidates,
  result,
}: {
  candidates: WorktreeCleanupCandidate[];
  result: WorktreeCleanupResult;
}) {
  const count = (outcome: string) => result.results.filter((r) => r.outcome === outcome).length;
  const titles = new Map(candidates.map((c) => [c.runId, c.workItemTitle]));
  const notCleaned = result.results.filter((r) => r.outcome !== "Cleaned");

  return (
    <div className="storage-cleanup-result" role="status">
      <p>
        Cleaned up {count("Cleaned")}, skipped {count("Skipped")}, failed {count("Failed")}.{" "}
        {result.totalBytes === null
          ? "Space freed: not measured."
          : `Freed ${formatBytes(result.totalBytes)}.`}
      </p>
      {notCleaned.length > 0 && (
        <ul>
          {notCleaned.map((r) => (
            <li key={r.runId}>
              {titles.get(r.runId) ?? r.runId}: {r.outcome === "Skipped" ? "skipped" : "failed"}
              {r.reason && ` (${r.reason})`}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
