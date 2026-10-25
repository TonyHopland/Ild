# ADR-0029: Backup and restore are two encrypted app-level exports

A logged-in user can take a backup of ILD's data and restore it on the same or a new
instance, from Settings > Storage. The first use is the PostgreSQL 17 to 18 upgrade:
back up on 17, start a fresh 18 volume, restore. A hand-run `pg_dumpall` or
`pg_upgrade` is what this replaces.

ILD has two databases, and a backup is **two separate flows, never one combined file**:

- `core` (`IldCore`): ILD's own data, backed up by ILD itself.
- `workitems` (`IldWorkitems`): the built-in WorkItem Server's data, backed up through
  new, non-contract endpoints on that server.

Each has its own file, its own passphrase and its own controls. An external tracker
(ADR-0025) has no such endpoints and its backup is reported unavailable, with that reason.

## What is in the databases

Investigated in the entities and migrations, not assumed.

| Where | What | Protection at rest |
| --- | --- | --- |
| `RemoteProviders` | `ApiKey`, `WebhookSecret` | `enc.v1` with `ILD_SECRET_KEY`, if set |
| `AiProviders` | `ApiKey` | `enc.v1` with `ILD_SECRET_KEY`, if set |
| `AiProviders` | `Config` (JSON, adapter settings) | **plaintext** |
| `Repositories` | `PreviewEnv` (a custom `.env`) | `enc.v1` with `ILD_SECRET_KEY`, if set |
| `PackageFeeds` | `Pat` (#217) | `enc.v1` with `ILD_SECRET_KEY`, if set |
| `Users` | `PasswordHash` | hashed: PBKDF2-SHA256, 100,000 iterations, salt per user |
| `UserSessions` | `TokenHash` | hashed: HMAC-SHA256 keyed on `ILD_SESSION_TOKEN_PEPPER`, bare SHA-256 without it |
| `UserSessions` | `UserAgent`, `CreatedFromIp` | **plaintext**, personal data |
| `AppSettings` | all settings, including `workItemServer.apiKey` | **plaintext** |
| `Repositories`, `RemoteProviders` | URLs, names, branch settings | **plaintext** |
| `ChatMessages`, `ChatSchedules` | conversations and prompts | **plaintext** |
| `EventLogs`, `LoopRunNodes`, `LoopRunVariables` | run logs, prompts, outputs | **plaintext**; can hold anything an agent printed (the risk accepted in #217) |
| `AdapterSessionSnapshotChunks` | agent transcripts | **plaintext** (compressed) |
| `LoopTemplates*`, `GlobalVariables`, `UserPreferences`, `Network*` | templates, variables, preferences, egress log | **plaintext** |
| `WorkItems`, `WorkItemEditProposals`, `WorkItemHistoryEntries` (workitems) | titles, descriptions, history | **plaintext** |
| `WorkItemAttachments` (workitems) | file content (`bytea`) | **plaintext** |

The WorkItem Server's database holds no secret store. Its API key lives in `.env`
(`WORKITEM_API_KEYS`) and, on the ILD side, in `AppSettings`.

## Decisions

**The file is encrypted with a passphrase, always.** Most of the table above is plaintext
in the database, so the only protection a backup can give those parts is the file's own.
AES-256-GCM in 64 KiB chunks under a key from PBKDF2-SHA256 (600,000 iterations, random
salt per file), BCL only. Every chunk is authenticated with its position and whether it
is the last, and the header with every chunk, so a truncated, reordered or altered file
fails; a wrong passphrase fails on the first chunk, before any data is read. The
passphrase is at least 12 characters, enforced by the API and shown in the UI, and is
never stored or logged. Argon2id and `age` were left out: neither is in the BCL, and a
new dependency was not worth it for a file whose weakest part is the passphrase a person
types.

**Secret keys are never in a backup.** `ILD_SECRET_KEY`, the session pepper, the database
passwords and `.env` stay with the operator. The backup holds data.

**`ILD_SECRET_KEY` stays optional.** #108 said "consider" requiring it; it was not made
required (`docker-compose.yml` defaults it to empty and startup logs a warning). Without
it the credentials in the table above are plaintext in the database and so in the backup.
The core backup therefore does not refuse: the UI shows the warning first and the user
acknowledges it before the backup can start. That acknowledgement is UI-only.

**`enc.v1` values are exported and imported as stored ciphertext, never decrypted.** The
export is PostgreSQL's text `COPY`, which bypasses EF Core's value converters. A restore
onto an instance with a different `ILD_SECRET_KEY` would leave every stored credential
unreadable without any error, so the core backup carries a *key check*: a fixed string
protected with the key. A core restore whose key check does not open with this instance's
key (or that has one onto an instance without a key) is refused, naming the mismatch,
before anything is read for import. A core backup made without a key carries none and
restores anywhere. Only the core backup has one; the WorkItem Server holds no encrypted values.

**A different session pepper only invalidates sessions.** Everyone signs in again; nothing
else about a session depends on it (#108).

**App-level export, not `pg_dump`.** Per-database `COPY` of every table the connected role
owns in the `public` schema, plus a manifest. The schema is not exported: an import drops
the tables and has EF's migrator rebuild them to the migration level the backup was made
at, loads the rows, and the application migrates forward on its next start. That is what
lets a backup from an older ILD, or from PostgreSQL 17, restore into a newer schema on 18,
with no `pg_dump`/`pg_restore` binaries in the image and no dependence on a dump format.
Roles and passwords come from the init script on the new cluster. Foreign keys are
satisfied by loading parents first; sequences are advanced past the loaded rows.

**Files outside the databases are not included**: session snapshot content on disk, run
worktrees, repository clones and preview data. They are rebuilt or re-cloned; the backup is
of the data that cannot be.

**Restore is guarded.**
- The user types `RESTORE`, enforced by the API.
- The whole file is decrypted and checked before anything is touched. A backup of the
  other database, from a newer ILD (a migration this build does not know, or a higher
  semantic version; an unparsable version is judged by its migrations alone), or altered
  or cut short, is refused with the data as it was.
- Nothing may be running that the data belongs to: a loop run, a worktree preview or a
  chat turn is a 409. While a restore runs, other API requests get 503 and the scheduler,
  throttled-resume sweeper, stuck-run watchdog, PR status poller and chat schedule
  scheduler skip their passes; run launch refuses as a backstop.
- The data being replaced is first stored as an encrypted backup (same passphrase), kept
  for download, one per database, replacing the previous.
- The import is one transaction. A failure leaves the data as it was.
- After a core restore ILD stops itself and the container restarts it; startup migrates
  and runs the data migrators. After a workitems restore the WorkItem Server migrates forward.

**Any logged-in user, never the agent.** There is no admin role (#63) and none is added.
The endpoints carry no authorization attribute, so the user-only fallback policy refuses
the agent token (403) as it does everywhere it has not been admitted. Backup and restore,
and the download of the pre-restore backup, are logged with the username.

**Where the files live.** Backups reach the user as a download and a restore as an
upload; ILD keeps only the pre-restore backup and the upload being worked on, in
`{data root}/backups` (0700, files 0600): ILD's own user, nowhere served, nowhere the
agent uid reads (ADR-0014). The upload spool is removed on success, failure and
cancellation, and at startup if the process died. The download is held in the browser's memory.

**The WorkItem Server's side (outside the contract).** `GET /maintenance/backup`,
`POST /maintenance/backup/export` and `POST /maintenance/backup/import` sit behind its API
key; the archive crosses unencrypted over that link, which already reads every work item,
and ILD encrypts it. The import is a raw octet-stream with the request-size limit lifted
(Kestrel's 30 MB default would refuse attachments).

## Two databases, and the order that follows

The two backups are taken at different moments, so a restore of one without the other can
be inconsistent. The procedure that keeps them together:

- **Back up** with the scheduler paused and no run executing.
- **Restore** with the scheduler paused: work items first, then ILD data, within 15 minutes.

Why that order: on start ILD's reconciler cancels any run whose work item is gone, Done or
reclaimed by the server, so the work items must exist, claimed, before ILD data is restored. The WorkItem Server's stale-claim
reclaimer hands a Running or WaitingForIld item back to Ready 15 minutes after its last
heartbeat, and the heartbeats in a backup date from when it was made. The work item import
therefore sets every such heartbeat to the import time, and the reclaimer and the import
are mutually exclusive (a pass skips while an import runs; an import waits for a pass
already running), so no pass can reset them between the commit and that refresh. ILD's own
heartbeats take over once the core restore has restarted it, which is the 15 minutes.
The order is documented and shown in the UI, not enforced in code.

## Gaps accepted

- Plaintext credentials with no `ILD_SECRET_KEY`, and `AiProviders.Config` and
  `AppSettings` always (the WorkItem Server API key among them), are in the backup in
  plaintext inside the encrypted file.
- A backup made while runs are executing is not refused; it is consistent per database
  (one repeatable-read transaction) but a parked run's state can be ahead of or behind its work item.
- Large backups pass through the browser's memory.
- No scheduled backups, retention or list of backups; if that grows it moves to its own tab.
