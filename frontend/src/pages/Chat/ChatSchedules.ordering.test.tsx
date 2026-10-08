import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import type { ChatSchedule } from "../../types";

const { handlers, service, settings } = vi.hoisted(() => ({
  handlers: {} as Record<string, Set<(msg: { payload: unknown }) => void>>,
  service: {
    listEvery: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
    delete: vi.fn(),
    runNow: vi.fn(),
  },
  settings: { get: vi.fn() },
}));

// Stable across renders, as the real hook's callbacks are.
vi.mock("../../hooks/useSignalR", () => {
  const hub = {
    connectionState: "connected",
    on: (event: string, handler: (msg: { payload: unknown }) => void) => {
      (handlers[event] ??= new Set()).add(handler);
    },
    off: (event: string, handler: (msg: { payload: unknown }) => void) => {
      handlers[event]?.delete(handler);
    },
    invoke: () => Promise.resolve(),
  };
  return { useSignalR: () => hub };
});

vi.mock("../../services/auth", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../services/auth")>()),
  chatScheduleService: service,
  settingsService: settings,
  repositoryService: { getEvery: () => Promise.resolve([]) },
  aiProviderService: { getAll: () => Promise.resolve([]) },
}));

import ChatSchedules from "./ChatSchedules";

function schedule(enabled: boolean, name = "Weekly retro"): ChatSchedule {
  return {
    id: "s1",
    name,
    prompt: "Check.",
    aiTag: null,
    cronExpression: "0 8 * * 1",
    timeZone: "UTC",
    enabled,
    repositoryScope: "All",
    repositoryIds: [],
    continueSession: true,
    latestChatSessionId: null,
    nextFireAt: null,
    lastFiring: null,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

function emit(event: string, payload: unknown) {
  act(() => {
    for (const handler of Array.from(handlers[event] ?? [])) handler({ payload });
  });
}

beforeEach(() => {
  settings.get.mockResolvedValue({ key: "scheduler.isPaused", value: "false" });
});

afterEach(() => {
  cleanup();
  for (const k of Object.keys(handlers)) delete handlers[k];
  vi.resetAllMocks();
});

const renderList = () =>
  render(
    <MemoryRouter>
      <ChatSchedules />
    </MemoryRouter>,
  );

const toggle = () => screen.getByRole("checkbox", { name: /^Enabled: / }) as HTMLInputElement;

describe("The schedules list orders what it shows", () => {
  test("a schedules hint re-reads the list", async () => {
    service.listEvery.mockResolvedValue([schedule(true)]);
    renderList();
    await screen.findByText("Weekly retro");

    service.listEvery.mockResolvedValue([schedule(true, "Renamed elsewhere")]);
    emit("ChatSchedulesChanged", { scheduleId: "s1" });

    expect(await screen.findByText("Renamed elsewhere")).toBeTruthy();
  });

  test("a read started before a save settled never shows over the read that follows the save", async () => {
    service.listEvery.mockResolvedValue([schedule(true)]);
    renderList();
    await screen.findByText("Weekly retro");
    await waitFor(() => expect(toggle().checked).toBe(true));
    // The read on mount and the one once the inbox is joined.
    await waitFor(() => expect(service.listEvery).toHaveBeenCalledTimes(2));

    const staleRead = deferred<ChatSchedule[]>();
    service.listEvery.mockReturnValueOnce(staleRead.promise);
    emit("ChatSchedulesChanged", { scheduleId: "s1" });

    const save = deferred<ChatSchedule>();
    const readAfterSave = deferred<ChatSchedule[]>();
    service.update.mockReturnValueOnce(save.promise);
    service.listEvery.mockReturnValueOnce(readAfterSave.promise);
    fireEvent.click(toggle());
    await act(async () => save.resolve(schedule(false)));

    await act(async () => staleRead.resolve([schedule(true, "Stale copy")]));
    expect(screen.queryByText("Stale copy")).toBeNull();

    await act(async () => readAfterSave.resolve([schedule(false)]));
    await waitFor(() => expect(toggle().checked).toBe(false));
  });

  test("a failed Run now shows its error on its own row and leaves the toggle free", async () => {
    service.listEvery.mockResolvedValue([schedule(true)]);
    const run = deferred<never>();
    service.runNow.mockReturnValueOnce(run.promise);
    renderList();
    await screen.findByText("Weekly retro");

    fireEvent.click(screen.getByRole("button", { name: "Run now" }));
    expect(toggle().disabled).toBe(false);
    expect((screen.getByRole("button", { name: "Run now" }) as HTMLButtonElement).disabled).toBe(
      true,
    );
    await act(async () => run.reject({ message: "boom" }));
    const row = screen.getByText("Weekly retro").closest("article") as HTMLElement;
    expect(await within(row).findByText("boom")).toBeTruthy();
  });

  test("a confirmed save shows even when the re-read after it fails", async () => {
    service.listEvery.mockResolvedValue([schedule(true)]);
    renderList();
    await waitFor(() => expect(service.listEvery).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(toggle().checked).toBe(true));

    service.update.mockResolvedValueOnce(schedule(false));
    service.listEvery.mockRejectedValue({ message: "offline" });
    fireEvent.click(toggle());

    await waitFor(() => expect(toggle().checked).toBe(false));
    await screen.findByText("offline");
    expect(toggle().checked).toBe(false);
  });

  test("one write of a schedule at a time: its open form and a save in flight hold the others", async () => {
    service.listEvery.mockResolvedValue([schedule(true)]);
    renderList();
    await screen.findByText("Weekly retro");
    const edit = () => screen.getByRole("button", { name: "Edit" }) as HTMLButtonElement;
    const remove = () => screen.getByRole("button", { name: "Delete" }) as HTMLButtonElement;

    const save = deferred<ChatSchedule>();
    service.update.mockReturnValueOnce(save.promise);
    fireEvent.click(toggle());
    expect(edit().disabled).toBe(true);
    expect(remove().disabled).toBe(true);
    await act(async () => save.resolve(schedule(false)));
    await waitFor(() => expect(edit().disabled).toBe(false));

    fireEvent.click(edit());
    await screen.findByRole("dialog");
    expect(toggle().disabled).toBe(true);
    expect(remove().disabled).toBe(true);
  });

  test("a form closed mid-save holds the schedule's other writes until its answer is in", async () => {
    service.listEvery.mockResolvedValue([schedule(true)]);
    renderList();
    await screen.findByText("Weekly retro");
    const remove = () => screen.getByRole("button", { name: "Delete" }) as HTMLButtonElement;

    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByLabelText(/^name/i), { target: { value: "Renamed" } });
    const save = deferred<ChatSchedule>();
    service.update.mockReturnValueOnce(save.promise);
    fireEvent.click(within(dialog).getByRole("button", { name: /^save/i }));
    fireEvent.click(within(dialog).getByRole("button", { name: "Close" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    expect(toggle().disabled).toBe(true);
    expect(remove().disabled).toBe(true);
    service.listEvery.mockResolvedValue([schedule(true, "Renamed")]);
    await act(async () => save.resolve(schedule(true, "Renamed")));
    await waitFor(() => expect(toggle().disabled).toBe(false));
    expect(remove().disabled).toBe(false);
    expect(screen.getByText("Renamed")).toBeTruthy();
  });

  test("Run now's answer links the chat it ran in even when the re-read after it fails", async () => {
    service.listEvery.mockResolvedValue([schedule(true)]);
    renderList();
    await waitFor(() => expect(service.listEvery).toHaveBeenCalledTimes(2));
    service.runNow.mockResolvedValueOnce({
      id: "f1",
      trigger: "RunNow",
      scheduledFor: null,
      firedAt: "2026-07-06T06:00:00Z",
      outcome: "Running",
      reason: null,
      chatSessionId: "c9",
      createdWorkItemIds: [],
      unresolvedItems: 0,
    });
    service.listEvery.mockRejectedValue({ message: "offline" });

    fireEvent.click(screen.getByRole("button", { name: "Run now" }));

    const link = await screen.findByRole("link", { name: /chat/i });
    expect(link.getAttribute("href")).toBe("/chat/c9");
  });

  test("hints that arrive while a read is out cost one more read, not one each", async () => {
    service.listEvery.mockResolvedValue([schedule(true)]);
    renderList();
    await waitFor(() => expect(service.listEvery).toHaveBeenCalledTimes(2));

    const slow = deferred<ChatSchedule[]>();
    service.listEvery.mockReturnValueOnce(slow.promise);
    emit("ChatSchedulesChanged", { scheduleId: "s1" });
    emit("ChatSchedulesChanged", { scheduleId: "s1" });
    emit("ChatSchedulesChanged", { scheduleId: "s1" });
    expect(service.listEvery).toHaveBeenCalledTimes(3);

    await act(async () => slow.resolve([schedule(true)]));
    await waitFor(() => expect(service.listEvery).toHaveBeenCalledTimes(4));
    await act(async () => {});
    expect(service.listEvery).toHaveBeenCalledTimes(4);
  });

  test("a scheduler event is a hint: the pause shown is the one the server reads back", async () => {
    service.listEvery.mockResolvedValue([]);
    renderList();
    await waitFor(() => expect(settings.get).toHaveBeenCalled());

    emit("SchedulerStateChanged", { isPaused: true, maxConcurrent: 5 });

    await waitFor(() => expect(settings.get).toHaveBeenCalledTimes(2));
    expect(screen.queryByText(/paused/i)).toBeNull();
  });
});
