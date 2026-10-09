import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { Link } from "react-router";
import { AiProviderTagField } from "../../components/AiProviderTagField";
import { useSignalR } from "../../hooks/useSignalR";
import { aiProviderService, chatScheduleService, repositoryService } from "../../services/auth";
import type {
  AiProvider,
  ChatSchedule,
  ChatScheduleFiring,
  ChatScheduleInput,
  ChatScheduleList,
  ChatScheduleRepositoryScope,
  Repository,
} from "../../types";
import { ROUTES } from "../../utils/constants";
import { CRON_PRESETS, describeCron } from "../../utils/cronText";
import { Switch } from "../Settings/controls";

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

function repositoriesText(schedule: ChatSchedule, repositories: Repository[] | null): string {
  if (schedule.repositoryScope === "All") return "All repositories";
  if (schedule.repositoryScope === "None") return "None";
  if (!repositories) return `${schedule.repositoryIds.length} selected`;
  return schedule.repositoryIds
    .map((id) => repositories.find((r) => r.id === id)?.name ?? id)
    .join(", ");
}

function LastFiring({ firing }: { firing: ChatScheduleFiring | null }) {
  if (!firing) return <>Never</>;
  return (
    <>
      <span className={`chat-schedule-outcome is-${firing.outcome.toLowerCase()}`}>
        {firing.outcome}
      </span>
      {firing.reason && `: ${firing.reason}`} · {formatTime(firing.firedAt)}
    </>
  );
}

/**
 * Runs one write to a schedule and, once it is in, hands the list the change it
 * made. Rejects with the server's error.
 */
type ScheduleWrite = (
  scheduleId: string,
  request: () => Promise<(schedules: ChatSchedule[]) => ChatSchedule[]>,
) => Promise<void>;

/** One schedule's row. Keyed by the schedule, so its error and delete confirmation are its own. */
function ScheduleRow({
  schedule,
  repositories,
  writing,
  editing,
  onEdit,
  write,
}: {
  schedule: ChatSchedule;
  repositories: Repository[] | null;
  /** A write to this schedule is under way, so its controls wait for it. */
  writing: boolean;
  /** Its form is open, which would save over a toggle or find it deleted. */
  editing: boolean;
  onEdit: () => void;
  write: ScheduleWrite;
}) {
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const deleteRef = useRef<HTMLButtonElement>(null);
  const cancelRef = useRef<HTMLButtonElement>(null);
  // The confirmation takes the place of the Delete button that opened it, so focus
  // would fall to the page: it moves to Cancel, and back to Delete when called off.
  // A layout effect, so focus moves in the commit that shows the change, not after it.
  const focusNextRef = useRef<"cancel" | "delete" | null>(null);
  useLayoutEffect(() => {
    const next = focusNextRef.current;
    focusNextRef.current = null;
    (next === "cancel" ? cancelRef : next === "delete" ? deleteRef : null)?.current?.focus();
  }, [confirmingDelete]);
  const askToDelete = () => {
    focusNextRef.current = "cancel";
    setConfirmingDelete(true);
  };
  // Not while a write runs: the Delete it would hand focus back to is disabled then,
  // and a delete under way could still fail and need its confirmation.
  const keepIt = () => {
    if (writing) return;
    focusNextRef.current = "delete";
    setConfirmingDelete(false);
  };

  const act = (fallback: string, request: Parameters<ScheduleWrite>[1]) => {
    setError(null);
    write(schedule.id, request).catch((e: unknown) => setError(failure(e, fallback)));
  };

  const facts: [string, React.ReactNode][] = [
    ["When", `${describeCron(schedule.cronExpression)} (${schedule.timeZone})`],
    ["AI tag", schedule.aiTag || "Default provider"],
    ["Repositories", repositoriesText(schedule, repositories)],
    ["Next firing", schedule.nextFireAt ? formatTime(schedule.nextFireAt) : "None"],
    ["Last firing", <LastFiring firing={schedule.lastFiring} />],
  ];

  return (
    <article className="chat-schedule">
      <header className="chat-schedule-head">
        <h2 className="chat-schedule-name">{schedule.name}</h2>
        <Switch
          checked={schedule.enabled}
          label={`Enabled: ${schedule.name}`}
          disabled={writing || editing}
          onChange={(enabled) =>
            act("The schedule could not be saved.", async () => {
              const saved = await chatScheduleService.update(schedule.id, {
                ...toInput(schedule),
                enabled,
              });
              return (list) => list.map((s) => (s.id === saved.id ? saved : s));
            })
          }
        />
      </header>
      <dl className="chat-schedule-facts">
        {facts.map(([term, value]) => (
          <div key={term}>
            <dt>{term}</dt> <dd>{value}</dd>{" "}
          </div>
        ))}
      </dl>
      {error && (
        <p className="chat-error" role="alert">
          {error}
        </p>
      )}
      <div className="chat-schedule-actions">
        <button
          type="button"
          className="btn btn-secondary btn-sm"
          disabled={writing}
          onClick={() =>
            act("The schedule could not be run.", async () => {
              const firing = await chatScheduleService.runNow(schedule.id);
              // A read that already holds this firing may know more about how it went.
              return (list) =>
                list.map((s) =>
                  s.id === schedule.id && s.lastFiring?.id !== firing.id
                    ? {
                        ...s,
                        lastFiring: firing,
                        latestChatSessionId: firing.chatSessionId ?? s.latestChatSessionId,
                      }
                    : s,
                );
            })
          }
        >
          Run now
        </button>
        <button
          type="button"
          className="btn btn-secondary btn-sm"
          disabled={writing}
          onClick={onEdit}
        >
          Edit
        </button>
        {confirmingDelete ? (
          <span
            className="chat-schedule-confirm"
            role="alertdialog"
            aria-label={`Delete the schedule ${schedule.name}?`}
            onKeyDown={(e) => {
              if (e.key !== "Escape") return;
              e.stopPropagation();
              keepIt();
            }}
          >
            Delete this schedule? Its chats stay.
            {/* Stays up until the delete succeeds, which takes the row with it: a failed
                delete leaves the confirmation, its error and focus where they were. Both
                buttons are aria-disabled while it runs rather than disabled, which would
                drop focus to the page. */}
            <button
              type="button"
              className="btn btn-danger btn-sm"
              aria-disabled={writing}
              onClick={() => {
                if (writing) return;
                act("The schedule could not be deleted.", async () => {
                  await chatScheduleService.delete(schedule.id);
                  return (list) => list.filter((s) => s.id !== schedule.id);
                });
              }}
            >
              Delete
            </button>
            <button
              type="button"
              ref={cancelRef}
              className="btn btn-secondary btn-sm"
              aria-disabled={writing}
              onClick={keepIt}
            >
              Cancel
            </button>
          </span>
        ) : (
          <button
            type="button"
            ref={deleteRef}
            className="btn btn-danger btn-sm"
            disabled={writing || editing}
            onClick={askToDelete}
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

/** Creates a schedule, or edits the one it was opened on. Keyed by each opening. */
function ScheduleForm({
  schedule,
  providers,
  repositories,
  onClose,
  onSave,
}: {
  schedule: ChatSchedule | null;
  providers: AiProvider[] | null;
  repositories: Repository[] | null;
  onClose: () => void;
  /** Saves the form; rejects with the server's error, and the form stays open. */
  onSave: (input: ChatScheduleInput) => Promise<void>;
}) {
  const [input, setInput] = useState<ChatScheduleInput>(() =>
    schedule
      ? toInput(schedule)
      : {
          name: "",
          prompt: "",
          aiTag: "",
          cronExpression: CRON_PRESETS[2].cron,
          timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
          enabled: true,
          repositoryScope: "All",
          repositoryIds: [],
          continueSession: true,
        },
  );
  const nameRef = useRef<HTMLInputElement>(null);
  // Keyed by each opening, so this runs once per opening: focus goes into the form
  // and, however it closes, back to whatever opened it.
  useLayoutEffect(() => {
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    nameRef.current?.focus();
    return () => {
      if (opener?.isConnected) opener.focus();
    };
  }, []);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const set = (changes: Partial<ChatScheduleInput>) =>
    setInput((prev) => ({ ...prev, ...changes }));
  const when = describeCron(input.cronExpression);

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
    try {
      await onSave({
        ...input,
        repositoryIds: input.repositoryScope === "Selected" ? input.repositoryIds : [],
      });
    } catch (e) {
      setError(failure(e, "The schedule could not be saved."));
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
        onKeyDown={(e) => {
          if (e.key !== "Escape") return;
          e.stopPropagation();
          onClose();
        }}
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
              ref={nameRef}
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
              A scheduled chat only checks: it files a work item for anything that needs changing.
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
              {CRON_PRESETS.map((preset) => (
                <button
                  key={preset.label}
                  type="button"
                  className="btn btn-secondary btn-sm"
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
              {when !== input.cronExpression
                ? when
                : "Minute, hour, day of month, month and day of week (0 or 7 is Sunday)."}
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
            Continue its chat: each firing is a new turn in the schedule's chat, until that chat is
            deleted or the AI tag picks another provider
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

/**
 * The signed-in user's chat schedules, in the Chat tab beside the chat list. The
 * server's list is the state; hub events only say when to read it again.
 */
export default function Schedules() {
  const [list, setList] = useState<ChatScheduleList | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [repositories, setRepositories] = useState<Repository[] | null>(null);
  const [providers, setProviders] = useState<AiProvider[] | null>(null);
  const [form, setForm] = useState<{ opening: number; schedule: ChatSchedule | null } | null>(null);
  const openingsRef = useRef(0);
  const openForm = (schedule: ChatSchedule | null) =>
    setForm({ opening: ++openingsRef.current, schedule });

  // Reads are numbered as they go out, and an answer is applied only if no later
  // read, and no write confirmed since it went out, has been applied: an older
  // answer could put back a schedule that is gone or a toggle that has moved.
  const readRef = useRef(0);
  const appliedRef = useRef(0);
  const reload = useCallback(async () => {
    const read = ++readRef.current;
    try {
      const next = await chatScheduleService.list();
      if (read <= appliedRef.current) return;
      appliedRef.current = read;
      setList(next);
      setLoadError(null);
    } catch (e) {
      if (read > appliedRef.current) setLoadError(failure(e, "Could not load the schedules."));
    }
  }, []);

  const confirm = useCallback(
    (change: (schedules: ChatSchedule[]) => ChatSchedule[]) => {
      appliedRef.current = readRef.current;
      setList((current) => current && { ...current, schedules: change(current.schedules) });
      void reload();
    },
    [reload],
  );

  // The schedules with a write under way; each settles only its own.
  const [writing, setWriting] = useState<ReadonlySet<string>>(new Set());
  const write = useCallback<ScheduleWrite>(
    async (scheduleId, request) => {
      setWriting((ids) => new Set(ids).add(scheduleId));
      try {
        confirm(await request());
      } catch (e) {
        void reload();
        throw e;
      } finally {
        setWriting((ids) => {
          const next = new Set(ids);
          next.delete(scheduleId);
          return next;
        });
      }
    },
    [confirm, reload],
  );

  useEffect(() => {
    void reload();
    repositoryService.getEvery().then(setRepositories, (e: unknown) => console.error(e));
    aiProviderService.getAll().then(setProviders, (e: unknown) => console.error(e));
  }, [reload]);

  const chatHub = useSignalR("/hubs/chat");
  useEffect(() => {
    const onChanged = () => void reload();
    chatHub.on("ChatSchedulesChanged", onChanged);
    return () => chatHub.off("ChatSchedulesChanged", onChanged);
  }, [chatHub.on, chatHub.off, reload]);
  // Hints go to the owner's inbox, which this connection joins; a read once it has
  // catches up on whatever was sent while it was away.
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
      void chatHub.invoke("UnsubscribeFromChatInbox")?.catch((err: unknown) => console.error(err));
    };
  }, [chatHub.connectionState, chatHub.invoke, reload]);

  // The global pause is part of the list, so pausing or resuming re-reads it.
  const workItemHub = useSignalR();
  useEffect(() => {
    const onSchedulerStateChanged = () => void reload();
    workItemHub.on("SchedulerStateChanged", onSchedulerStateChanged);
    return () => workItemHub.off("SchedulerStateChanged", onSchedulerStateChanged);
  }, [workItemHub.on, workItemHub.off, reload]);

  const schedules = list?.schedules ?? null;
  return (
    <section className="chat-page-chat chat-schedules">
      <header className="chat-page-header chat-schedules-header">
        <h1 className="chat-page-title">Schedules</h1>
        <button type="button" className="chat-primary-btn" onClick={() => openForm(null)}>
          New schedule
        </button>
      </header>
      <div className="chat-schedules-body">
        {list?.schedulerPaused && (
          <p className="chat-schedules-paused" role="status">
            The scheduler is paused: no schedule fires until it is resumed. Run now still works.
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
              writing={writing.has(schedule.id)}
              editing={form?.schedule?.id === schedule.id}
              onEdit={() => openForm(schedule)}
              write={write}
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
          onSave={async (input) => {
            const edited = form.schedule;
            if (edited) {
              await write(edited.id, async () => {
                const saved = await chatScheduleService.update(edited.id, input);
                return (list) => list.map((s) => (s.id === saved.id ? saved : s));
              });
            } else {
              const created = await chatScheduleService.create(input);
              confirm((list) => [...list.filter((s) => s.id !== created.id), created]);
            }
            // Closes only the form that saved, not one opened since this one closed.
            setForm((open) => (open?.opening === form.opening ? null : open));
          }}
        />
      )}
    </section>
  );
}
