import { useCallback, useEffect, useRef, useState } from "react";
import { storageService } from "../../../services/auth";
import type { ApiError, StorageOverview } from "../../../types";
import { formatBytes } from "../../../utils/attachments";
import PreviewDataDelete from "../../../components/PreviewDataDelete";

type Overview = { data: StorageOverview | null; loading: boolean; error: string | null };

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
          title="Some of it could not be read or was not measured, so the real figure may be higher."
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

/** How much disk ILD takes. */
export default function StorageSettings() {
  const [overview, setOverview] = useState<Overview>({ data: null, loading: true, error: null });
  const mounted = useRef(true);
  // Only the latest measurement may land: an earlier one finishing last would show older figures.
  const latestMeasurement = useRef(0);
  // A measurement walks the server's disk; one nobody will read is aborted, which cancels the walk.
  const measurementAbort = useRef<AbortController | null>(null);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      measurementAbort.current?.abort();
    };
  }, []);

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
            disabled={overview.loading}
          >
            Refresh
          </button>
        </div>
        <p className="settings-card-note">
          Worktrees exist only for runs still in progress or pinned: a run's worktree is removed
          when the run ends.
        </p>
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
                        {category.key === "preview-data" && entry.repositoryId && (
                          <PreviewDataDelete
                            key={entry.repositoryId}
                            repositoryId={entry.repositoryId}
                            wording={{
                              trigger: "Delete",
                              triggerLabel: `Delete preview data: ${entry.label}`,
                              confirmGroup: "Delete preview data",
                              warning:
                                "Deletes this repository's preview data for every work item.",
                              confirm: "Confirm delete",
                              done: "Deleted.",
                              failed: "Could not delete the preview data.",
                            }}
                            onDeleted={() => {
                              if (mounted.current) void measure();
                            }}
                          />
                        )}
                      </li>
                    ))}
                  </ul>
                )}
              </li>
            ))}
          </ul>
        )}
      </section>
    </>
  );
}
