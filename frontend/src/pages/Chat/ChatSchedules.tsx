import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router";
import { AiProviderTagField } from "../../components/AiProviderTagField";
import { useSignalR } from "../../hooks/useSignalR";
import {
  aiProviderService,
  chatScheduleService,
  repositoryService,
  SchedulerSettingKeys,
  settingsService,
} from "../../services/auth";
import type {
  AiProvider,
  ChatSchedule,
  ChatScheduleFiring,
  ChatScheduleInput,
  ChatScheduleRepositoryScope,
  Repository,
} from "../../types";
import { ROUTES } from "../../utils/constants";
import { cronText } from "../../utils/cronText";
import { Switch } from "../Settings/controls";

const PRESETS = [
  { label: "Hourly", cron: "0 * * * *" },
  { label: "Daily", cron: "0 9 * * *" },
  { label: "Weekly", cron: "0 9 * * 1" },
] as const;

const SCOPES: { value: ChatScheduleRepositoryScope; label: string }[] = [
  { value: "All", label: "All repositories, including ones added later" },
  { value: "Selected", label: "Selected repositories" },
  { value: "None", label: "No repositories" },
];

/** What the server said went wrong, else `fallback`. */
const failure = (error: unknown, fallback: string) =>
  (error as { message?: string } | null)?.message || fallback;

const formatTime = (iso: string) => new Date(iso).toLocaleString();

const toInput = (schedule: ChatSchedule): ChatScheduleInput => ({
  name: schedule.name,
  prompt: schedule.prompt,
  aiTag: schedule.aiTag ?? "",
  cronExpression: schedule.cronExpression,
  timeZone: schedule.timeZone,
  enabled: schedule.enabled,
  repositoryScope: schedule.repositoryScope,
  repositoryIds: schedule.repositoryIds,
  continueSession: schedule.continueSession,
});

/** The repositories a schedule may use, by name once they are known. */
function repositoriesText(schedule: ChatSchedule, repositories: Repository[] | null): string {
  if (schedule.repositoryScope === "All") return "All repositories";
  if (schedule.repositoryScope === "None") return "None";
  if (!repositories) return `${schedule.repositoryIds.length} selected`;
  return schedule.repositoryIds
    .map((id) => repositories.find((r) => r.id === id)?.name ?? id)
    .join(", ");
}

function LastFiring({ firing }: { firing: ChatScheduleFiring | null }) {
  if (!firing) return <>never</>;
  return (
    <>
      <span
        className={`chat-schedule-outcome chat-schedule-outcome-${firing.outcome.toLowerCase()}`}
      >
        {firing.outcome}
      </span>
      {firing.reason && `: ${firing.reason}`} · {formatTime(firing.firedAt)}
      {firing.createdWorkItemIds.length > 0 && (
        <>
          {" · Filed "}
          {firing.createdWorkItemIds.map((id, i) => (
            <span key={id}>
              {i > 0 && ", "}
              <Link to={`${ROUTES.TASKBOARD}/${id}`}>{id}</Link>
            </span>
          ))}
        </>
      )}
      {firing.unresolvedItems > 0 && ` · ${firing.unresolvedItems} unresolved`}
    </>
  );
}

/** One act a row can take, with its own busy flag and error, settled only by its own newest request. */
function useAct(fallback: string, onSettled: () => void) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const generationRef = useRef(0);
  const run = async (request: () => Promise<unknown>) => {
    const generation = ++generationRef.current;
    setBusy(true);
    setError(null);
    try {
      await request();
    } catch (e) {
      if (generationRef.current === generation) setError(failure(e, fallback));
    } finally {
      if (generationRef.current === generation) setBusy(false);
      onSettled();
    }
  };
  return { busy, error, run };
}

/** One schedule on the list. Keyed by the schedule, so its acts and delete confirmation are its own. */
function ScheduleRow({
  schedule,
  repositories,
  onEdit,
  onChanged,
}: {
  schedule: ChatSchedule;
  repositories: Repository[] | null;
  onEdit: () => void;
  /** A change this row asked for has settled, so the list is re-read. */
  onChanged: () => void;
}) {
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const toggle = useAct("The schedule could not be saved.", onChanged);
  const runNow = useAct("The schedule could not be run.", onChanged);
  const remove = useAct("The schedule could not be deleted.", onChanged);
  const when = cronText(schedule.cronExpression) ?? schedule.cronExpression;
  const facts: [string, React.ReactNode][] = [
    ["When", `${when} (${schedule.timeZone})`],
    ["AI tag", schedule.aiTag || "default"],
    ["Repositories", repositoriesText(schedule, repositories)],
    ["Next firing", schedule.nextFireAt ? formatTime(schedule.nextFireAt) : "none"],
    ["Last firing", <LastFiring firing={schedule.lastFiring} />],
  ];
  const errors = [toggle.error, runNow.error, remove.error].filter((e) => e !== null);

  return (
    <article className="chat-schedule">
      <header className="chat-schedule-head">
        <h2 className="chat-schedule-name">{schedule.name}</h2>
        <Switch
          checked={schedule.enabled}
          label={`Enabled: ${schedule.name}`}
          disabled={toggle.busy}
          onChange={() =>
            void toggle.run(() =>
              chatScheduleService.update(schedule.id, {
                ...toInput(schedule),
                enabled: !schedule.enabled,
              }),
            )
          }
        />
      </header>
      <dl className="chat-schedule-facts">
        {facts.map(([term, value]) => (
          // Spaced, so the row reads as words to whatever takes its text.
          <div key={term}>
            <dt>{term}</dt> <dd>{value}</dd>{" "}
          </div>
        ))}
      </dl>
      {errors.map((error) => (
        <p key={error} className="chat-error" role="alert">
          {error}
        </p>
      ))}
      <div className="chat-schedule-actions">
        <button
          type="button"
          className="btn btn-secondary btn-small"
          disabled={runNow.busy}
          onClick={() => void runNow.run(() => chatScheduleService.runNow(schedule.id))}
        >
          Run now
        </button>
        <button type="button" className="btn btn-secondary btn-small" onClick={onEdit}>
          Edit
        </button>
        {confirmingDelete ? (
          <span
            className="chat-schedule-confirm"
            role="alertdialog"
            aria-label={`Delete the schedule ${schedule.name}?`}
          >
            Delete this schedule? Its chats and the items it filed stay.
            <button
              type="button"
              className="btn btn-danger btn-small"
              onClick={() => {
                setConfirmingDelete(false);
                void remove.run(() => chatScheduleService.delete(schedule.id));
              }}
            >
              Delete
            </button>
            <button
              type="button"
              className="btn btn-small"
              onClick={() => setConfirmingDelete(false)}
            >
              Cancel
            </button>
          </span>
        ) : (
          <button
            type="button"
            className="btn btn-danger btn-small"
            disabled={remove.busy}
            onClick={() => setConfirmingDelete(true)}
          >
            Delete
          </button>
        )}
        {schedule.latestChatSessionId && (
          <Link
            className="chat-schedule-chat"
            to={`${ROUTES.CHAT}/${schedule.latestChatSessionId}`}
          >
            View chat
          </Link>
        )}
      </div>
    </article>
  );
}

/** Creates a schedule, or edits the one it was opened on. Keyed by that schedule. */
function ScheduleForm({
  schedule,
  providers,
  repositories,
  onClose,
  onSaved,
}: {
  schedule: ChatSchedule | null;
  providers: AiProvider[] | null;
  repositories: Repository[] | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [input, setInput] = useState<ChatScheduleInput>(() =>
    schedule
      ? toInput(schedule)
      : {
          name: "",
          prompt: "",
          aiTag: "",
          cronExpression: PRESETS[2].cron,
          timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
          enabled: true,
          repositoryScope: "All",
          repositoryIds: [],
          continueSession: true,
        },
  );
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const set = (changes: Partial<ChatScheduleInput>) =>
    setInput((prev) => ({ ...prev, ...changes }));
  const when = cronText(input.cronExpression);

  const toggleRepository = (id: string, on: boolean) =>
    setInput((prev) => ({
      ...prev,
      repositoryIds: on
        ? [...prev.repositoryIds, id]
        : prev.repositoryIds.filter((selected) => selected !== id),
    }));

  const save = async () => {
    setSaving(true);
    setError(null);
    const body = {
      ...input,
      repositoryIds: input.repositoryScope === "Selected" ? input.repositoryIds : [],
    };
    try {
      if (schedule) await chatScheduleService.update(schedule.id, body);
      else await chatScheduleService.create(body);
      onSaved();
    } catch (e) {
      setError(failure(e, "The schedule could not be saved."));
    } finally {
      setSaving(false);
    }
  };

  const title = schedule ? `Edit ${schedule.name}` : "New schedule";
  return (
    <div className="modal-overlay" onMouseDown={onClose}>
      <div
        className="modal-content chat-schedule-form"
        onMouseDown={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-label={title}
      >
        <div className="modal-header">
          <h2>{title}</h2>
          <button type="button" className="modal-close" aria-label="Close" onClick={onClose}>
            ×
          </button>
        </div>
        <div className="modal-body">
          <div className="form-group">
            <label htmlFor="schedule-name">Name</label>
            <input
              id="schedule-name"
              type="text"
              maxLength={120}
              value={input.name}
              onChange={(e) => set({ name: e.target.value })}
            />
          </div>
          <div className="form-group">
            <label htmlFor="schedule-prompt">Prompt</label>
            <textarea
              id="schedule-prompt"
              rows={6}
              value={input.prompt}
              onChange={(e) => set({ prompt: e.target.value })}
            />
            <span className="form-hint">
              A scheduled chat only checks: it files work items for what needs changing.
            </span>
          </div>
          <AiProviderTagField
            id="schedule-ai-tag"
            tag={input.aiTag}
            providers={providers}
            onChange={(aiTag) => set({ aiTag })}
            cannotRun="this schedule cannot run"
          />
          <div className="form-group">
            <div className="chat-schedule-presets" role="group" aria-label="Presets">
              {PRESETS.map((preset) => (
                <button
                  key={preset.label}
                  type="button"
                  className="btn btn-secondary btn-small"
                  onClick={() => set({ cronExpression: preset.cron })}
                >
                  {preset.label}
                </button>
              ))}
            </div>
            <label htmlFor="schedule-cron">Cron</label>
            <input
              id="schedule-cron"
              type="text"
              value={input.cronExpression}
              onChange={(e) => set({ cronExpression: e.target.value })}
            />
            <span className="form-hint">
              {when ?? "Minute, hour, day of month, month and day of week (0 or 7 is Sunday)."}
            </span>
          </div>
          <div className="form-group">
            <label htmlFor="schedule-zone">Time zone</label>
            <input
              id="schedule-zone"
              type="text"
              value={input.timeZone}
              onChange={(e) => set({ timeZone: e.target.value })}
            />
          </div>
          <div className="form-group">
            <label htmlFor="schedule-repositories">Repositories</label>
            <select
              id="schedule-repositories"
              value={input.repositoryScope}
              onChange={(e) =>
                set({ repositoryScope: e.target.value as ChatScheduleRepositoryScope })
              }
            >
              {SCOPES.map((scope) => (
                <option key={scope.value} value={scope.value}>
                  {scope.label}
                </option>
              ))}
            </select>
            {input.repositoryScope === "Selected" && (
              <div className="chat-schedule-repositories">
                {(repositories ?? []).map((repo) => (
                  <label key={repo.id} className="chat-schedule-check">
                    <input
                      type="checkbox"
                      checked={input.repositoryIds.includes(repo.id)}
                      onChange={(e) => toggleRepository(repo.id, e.target.checked)}
                    />
                    {repo.name}
                  </label>
                ))}
              </div>
            )}
          </div>
          <label className="chat-schedule-check">
            <input
              type="checkbox"
              checked={input.continueSession}
              onChange={(e) => set({ continueSession: e.target.checked })}
            />
            Continue one chat: each firing is a new turn in the same chat
          </label>
          <label className="chat-schedule-check">
            <input
              type="checkbox"
              checked={input.enabled}
              onChange={(e) => set({ enabled: e.target.checked })}
            />
            Enabled
          </label>
          {error && (
            <p className="chat-error" role="alert">
              {error}
            </p>
          )}
        </div>
        <div className="modal-footer">
          <button type="button" className="btn btn-secondary" onClick={onClose}>
            Cancel
          </button>
          <button
            type="button"
            className="btn btn-primary"
            disabled={saving}
            onClick={() => void save()}
          >
            {schedule ? "Save" : "Create"}
          </button>
        </div>
      </div>
    </div>
  );
}

/** The signed-in user's chat schedules, in the Chat tab beside the chat list. */
export default function ChatSchedules() {
  const [schedules, setSchedules] = useState<ChatSchedule[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [repositories, setRepositories] = useState<Repository[] | null>(null);
  const [providers, setProviders] = useState<AiProvider[] | null>(null);
  const [paused, setPaused] = useState(false);
  // The open form, numbered per opening, and the schedule it edits (null for a new one).
  const [form, setForm] = useState<{ opening: number; schedule: ChatSchedule | null } | null>(null);
  const openingsRef = useRef(0);
  const openForm = (schedule: ChatSchedule | null) =>
    setForm({ opening: ++openingsRef.current, schedule });

  // Reads are numbered as they go out. One is applied only if it is newer than
  // the last applied and was started after the last write settled, so neither a
  // slow read nor one from before a save puts back a list that has since changed.
  const readRef = useRef(0);
  const appliedReadRef = useRef(0);
  const staleThroughReadRef = useRef(0);
  const reload = useCallback(async () => {
    const read = ++readRef.current;
    const current = () => read > appliedReadRef.current && read > staleThroughReadRef.current;
    try {
      const list = await chatScheduleService.list();
      if (!current()) return;
      appliedReadRef.current = read;
      setSchedules(list);
      setLoadError(null);
    } catch (e) {
      if (current()) setLoadError(failure(e, "Could not load the schedules."));
    }
  }, []);
  const writeSettled = useCallback(() => {
    staleThroughReadRef.current = readRef.current;
    void reload();
  }, [reload]);

  useEffect(() => {
    void reload();
    repositoryService.getEvery().then(setRepositories, (e) => console.error(e));
    aiProviderService.getAll().then(setProviders, (e) => console.error(e));
  }, [reload]);

  // Hub events are hints to re-read, never state to apply: they carry no order,
  // and the hub drops whatever is sent while it is reconnecting.
  const chatHub = useSignalR("/hubs/chat");
  useEffect(() => {
    const onSchedulesChanged = () => void reload();
    chatHub.on("ChatSchedulesChanged", onSchedulesChanged);
    return () => chatHub.off("ChatSchedulesChanged", onSchedulesChanged);
  }, [chatHub.on, chatHub.off, reload]);
  useEffect(() => {
    if (chatHub.connectionState !== "connected") return;
    void (async () => {
      try {
        await chatHub.invoke("SubscribeToChatInbox");
        await reload();
      } catch (err) {
        console.error(err);
      }
    })();
    return () => {
      void chatHub.invoke("UnsubscribeFromChatInbox")?.catch((err) => console.error(err));
    };
  }, [chatHub.connectionState, chatHub.invoke, reload]);

  const pauseReadRef = useRef(0);
  const appliedPauseReadRef = useRef(0);
  const readPaused = useCallback(async () => {
    const read = ++pauseReadRef.current;
    try {
      const setting = await settingsService.get(SchedulerSettingKeys.IsPaused);
      if (read <= appliedPauseReadRef.current) return;
      appliedPauseReadRef.current = read;
      setPaused(setting.value === "true");
    } catch (e) {
      console.error(e);
    }
  }, []);
  const workItemHub = useSignalR();
  useEffect(() => {
    const onSchedulerStateChanged = () => void readPaused();
    workItemHub.on("SchedulerStateChanged", onSchedulerStateChanged);
    return () => workItemHub.off("SchedulerStateChanged", onSchedulerStateChanged);
  }, [workItemHub.on, workItemHub.off, readPaused]);
  useEffect(() => {
    if (workItemHub.connectionState === "connected") void readPaused();
  }, [workItemHub.connectionState, readPaused]);

  return (
    <section className="chat-page-chat chat-schedules">
      <header className="chat-page-header chat-schedules-header">
        <h1 className="chat-page-title">Schedules</h1>
        <button type="button" className="chat-primary-btn" onClick={() => openForm(null)}>
          New schedule
        </button>
      </header>
      <div className="chat-schedules-body">
        {paused && (
          <p className="chat-schedules-paused" role="status">
            The scheduler is paused, so no schedule fires until it is resumed. Run now still works.
          </p>
        )}
        {loadError && <div className="chat-error">{loadError}</div>}
        {schedules === null ? (
          !loadError && <p className="chat-muted">Loading…</p>
        ) : schedules.length === 0 ? (
          <p className="chat-muted">
            No schedules yet. A schedule starts a chat turn with its prompt on a timer, to check
            something and file work items for what it finds.
          </p>
        ) : (
          schedules.map((schedule) => (
            <ScheduleRow
              key={schedule.id}
              schedule={schedule}
              repositories={repositories}
              onEdit={() => openForm(schedule)}
              onChanged={writeSettled}
            />
          ))
        )}
      </div>
      {form && (
        <ScheduleForm
          key={form.opening}
          schedule={form.schedule}
          providers={providers}
          repositories={repositories}
          onClose={() => setForm(null)}
          onSaved={() => {
            // Closes only the form that saved, not one opened since it was closed.
            setForm((open) => (open?.opening === form.opening ? null : open));
            writeSettled();
          }}
        />
      )}
    </section>
  );
}
