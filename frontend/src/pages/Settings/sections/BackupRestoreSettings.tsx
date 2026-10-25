import { useCallback, useEffect, useId, useRef, useState } from "react";
import { storageService } from "../../../services/auth";
import type {
  ApiError,
  BackupDatabaseKey,
  BackupStatus,
  DatabaseBackupStatus,
  RestoreResult,
} from "../../../types";
import { formatBytes } from "../../../utils/attachments";
import { saveBlob } from "../../../utils/saveBlob";

/** The word the server requires before it replaces a database. */
const RESTORE_CONFIRMATION = "RESTORE";

const failure = (error: unknown, fallback: string) =>
  (error as ApiError | Error | undefined)?.message || fallback;

const DATABASES: { key: BackupDatabaseKey; heading: string; what: string }[] = [
  {
    key: "core",
    heading: "ILD data (core database)",
    what: "Templates, runs, chats, providers, settings and users.",
  },
  {
    key: "workitems",
    heading: "Work item data (WorkItem Server database)",
    what: "Work items, their history, attachments and proposed edits.",
  },
];

/** Each database's backup and restore. Keyed by database, so what is typed for one never reaches the other. */
function DatabaseBackup({
  database,
  status,
  secretKeyConfigured,
  minPassphraseLength,
  busy,
  setBusy,
  onRestored,
}: {
  database: (typeof DATABASES)[number];
  status: DatabaseBackupStatus;
  secretKeyConfigured: boolean;
  minPassphraseLength: number;
  busy: boolean;
  setBusy: (busy: boolean) => void;
  onRestored: (database: BackupDatabaseKey, result: RestoreResult) => void;
}) {
  const ids = useId();
  const [createPassphrase, setCreatePassphrase] = useState("");
  const [acknowledged, setAcknowledged] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);
  const [createNotice, setCreateNotice] = useState<string | null>(null);
  const [file, setFile] = useState<File | null>(null);
  const [fileInput, setFileInput] = useState(0);
  const [restorePassphrase, setRestorePassphrase] = useState("");
  const [confirmation, setConfirmation] = useState("");
  const [restoreError, setRestoreError] = useState<string | null>(null);
  const [downloadError, setDownloadError] = useState<string | null>(null);

  if (!status.available)
    return (
      <div className="backup-database">
        <h4 className="backup-database-heading">{database.heading}</h4>
        <p className="settings-card-note">{status.reason ?? "Backup is not available here."}</p>
      </div>
    );

  const needsAcknowledgement = database.key === "core" && !secretKeyConfigured;
  const canCreate =
    !busy &&
    createPassphrase.length >= minPassphraseLength &&
    (!needsAcknowledgement || acknowledged);
  const canRestore =
    !busy && file !== null && restorePassphrase.length > 0 && confirmation === RESTORE_CONFIRMATION;

  const create = async () => {
    setBusy(true);
    setCreateError(null);
    setCreateNotice(null);
    try {
      const { blob, fileName } = await storageService.createBackup(database.key, createPassphrase);
      const name = fileName ?? `ild-${database.key}.ildbackup`;
      saveBlob(blob, name);
      setCreatePassphrase("");
      setCreateNotice(`Saved ${name}.`);
    } catch (error) {
      setCreateError(failure(error, "The backup could not be created."));
    } finally {
      setBusy(false);
    }
  };

  const restore = async () => {
    if (!file) return;
    setBusy(true);
    setRestoreError(null);
    try {
      const form = new FormData();
      form.append("file", file, file.name);
      form.append("passphrase", restorePassphrase);
      form.append("confirmation", confirmation);
      const result = await storageService.restoreBackup(database.key, form);
      setFile(null);
      setFileInput((n) => n + 1);
      setRestorePassphrase("");
      setConfirmation("");
      onRestored(database.key, result);
    } catch (error) {
      // The confirmation is typed again for every attempt.
      setConfirmation("");
      setRestoreError(failure(error, "The restore failed."));
    } finally {
      setBusy(false);
    }
  };

  const downloadPreRestore = async () => {
    setBusy(true);
    setDownloadError(null);
    try {
      saveBlob(
        await storageService.downloadPreRestoreBackup(database.key),
        `ild-${database.key}-before-restore.ildbackup`,
      );
    } catch (error) {
      setDownloadError(failure(error, "The backup could not be downloaded."));
    } finally {
      setBusy(false);
    }
  };

  const preRestore = status.preRestoreBackup;

  return (
    <div className="backup-database">
      <h4 className="backup-database-heading">{database.heading}</h4>
      <p className="settings-card-note">{database.what}</p>

      <div className="backup-form">
        <label htmlFor={`${ids}-create`}>
          Passphrase for the new backup (at least {minPassphraseLength} characters)
        </label>
        <input
          id={`${ids}-create`}
          type="password"
          autoComplete="new-password"
          value={createPassphrase}
          onChange={(event) => setCreatePassphrase(event.target.value)}
        />
        {needsAcknowledgement && (
          <>
            <p className="settings-error">
              ILD_SECRET_KEY is not set, so the credentials ILD keeps (provider API keys, webhook
              secrets, package feed tokens) are stored in plaintext and will be in this backup in
              plaintext. The passphrase still protects the whole file, but anyone who has it, or the
              database, can read them.
            </p>
            <label className="backup-acknowledge">
              <input
                type="checkbox"
                checked={acknowledged}
                onChange={(event) => setAcknowledged(event.target.checked)}
              />
              I understand and want the backup anyway
            </label>
          </>
        )}
        <div className="backup-actions">
          <button
            type="button"
            className="btn btn-secondary"
            disabled={!canCreate}
            onClick={() => void create()}
          >
            Create backup…
          </button>
        </div>
        {createError && (
          <div className="settings-error" role="alert">
            {createError}
          </div>
        )}
        {createNotice && (
          <div className="settings-success" role="status">
            {createNotice}
          </div>
        )}
      </div>

      <div className="backup-form backup-restore">
        <label htmlFor={`${ids}-file`}>Backup file to restore</label>
        <input
          key={fileInput}
          id={`${ids}-file`}
          type="file"
          accept=".ildbackup"
          onChange={(event) => setFile(event.target.files?.[0] ?? null)}
        />
        <label htmlFor={`${ids}-restore`}>Passphrase the backup was made with</label>
        <input
          id={`${ids}-restore`}
          type="password"
          autoComplete="off"
          value={restorePassphrase}
          onChange={(event) => setRestorePassphrase(event.target.value)}
        />
        <label htmlFor={`${ids}-confirm`}>Type {RESTORE_CONFIRMATION} to confirm</label>
        <input
          id={`${ids}-confirm`}
          type="text"
          autoComplete="off"
          value={confirmation}
          onChange={(event) => setConfirmation(event.target.value)}
        />
        <div className="backup-actions">
          <button
            type="button"
            className="btn btn-danger"
            disabled={!canRestore}
            onClick={() => void restore()}
          >
            Restore…
          </button>
        </div>
        {restoreError && (
          <div className="settings-error" role="alert">
            {restoreError}
          </div>
        )}
      </div>

      {preRestore && (
        <div className="backup-form">
          <p className="settings-card-note">
            Before the last restore, ILD stored the data it was about to replace
            {` (${new Date(preRestore.createdAt).toLocaleString()}, ${formatBytes(preRestore.bytes)})`}
            . It is protected by the passphrase of that restore.
          </p>
          <div className="backup-actions">
            <button
              type="button"
              className="btn btn-secondary"
              disabled={busy}
              onClick={() => void downloadPreRestore()}
            >
              Download the data replaced by the last restore
            </button>
          </div>
          {downloadError && (
            <div className="settings-error" role="alert">
              {downloadError}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/**
 * Backup and restore of ILD's two databases, each its own file and passphrase. Apart from the disk
 * usage controls on purpose: a restore replaces data, and nothing here is an everyday cleanup.
 */
export default function BackupRestoreSettings({ onChanged }: { onChanged: () => void }) {
  const [status, setStatus] = useState<BackupStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [restarting, setRestarting] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const mounted = useRef(true);
  const loadAbort = useRef<AbortController | null>(null);

  const load = useCallback(async () => {
    loadAbort.current?.abort();
    const abort = new AbortController();
    loadAbort.current = abort;
    try {
      const loaded = await storageService.getBackupStatus(abort.signal);
      if (mounted.current && !abort.signal.aborted) {
        setStatus(loaded);
        setError(null);
      }
    } catch (failed) {
      if (mounted.current && !abort.signal.aborted)
        setError(failure(failed, "Could not load the backup status."));
    }
  }, []);

  useEffect(() => {
    mounted.current = true;
    void load();
    return () => {
      mounted.current = false;
      loadAbort.current?.abort();
    };
  }, [load]);

  const restored = (database: BackupDatabaseKey, result: RestoreResult) => {
    if (result.restarting) {
      setRestarting(true);
      setNotice(
        "ILD's data was restored. ILD is restarting to apply it: reload this page in a minute, then sign in again if you are asked to.",
      );
      return;
    }
    setNotice(`${database === "workitems" ? "Work item" : "ILD"} data was restored.`);
    void load();
    onChanged();
  };

  return (
    <section className="settings-card backup-restore-card">
      <div className="settings-card-header">
        <h3 className="settings-card-title">Backup and restore</h3>
      </div>
      <p className="settings-card-note">
        A backup is one encrypted file per database. Its passphrase is never stored: without it the
        file cannot be read, and ILD cannot recover it. A backup never contains ILD_SECRET_KEY, the
        session pepper or the database passwords. Keep your .env safe separately, because a restore
        needs the same ILD_SECRET_KEY.
      </p>
      <p className="settings-error backup-warning">
        A restore replaces all current data in that database and restarts ILD for its own data.
        Restoring one database without the other can leave them inconsistent. Restore both, in this
        order: make sure the scheduler is paused and no run is executing, restore the work items
        first, then the ILD data, within 15 minutes of each other. ILD cancels runs whose work item
        is missing, and the WorkItem Server hands claimed items back to Ready after 15 minutes
        without a heartbeat.
      </p>

      {error && (
        <div className="settings-error" role="alert">
          {error}{" "}
          <button type="button" className="btn btn-secondary" onClick={() => void load()}>
            Retry
          </button>
        </div>
      )}
      {notice && (
        <div className="settings-success" role="status">
          {notice}
        </div>
      )}
      {status &&
        DATABASES.map((database) => (
          <DatabaseBackup
            key={database.key}
            database={database}
            status={status.databases[database.key]}
            secretKeyConfigured={status.secretKeyConfigured}
            minPassphraseLength={status.minPassphraseLength}
            busy={busy || restarting}
            setBusy={setBusy}
            onRestored={restored}
          />
        ))}
    </section>
  );
}
