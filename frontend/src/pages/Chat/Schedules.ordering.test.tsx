import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import type { ChatSchedule, ChatScheduleList } from "../../types";

const { handlers, reads, chatScheduleService } = vi.hoisted(() => {
  const reads: { resolve: (list: ChatScheduleList) => void }[] = [];
  return {
    handlers: {} as Record<string, Set<() => void>>,
    reads,
    chatScheduleService: {
      list: vi.fn(
        () =>
          new Promise<ChatScheduleList>((resolve) => {
            reads.push({ resolve });
          }),
      ),
      update: vi.fn(),
      create: vi.fn(),
      delete: vi.fn(),
      runNow: vi.fn(),
    },
  };
});

vi.mock("../../hooks/useSignalR", () => ({
  useSignalR: () => ({
    connectionState: "disconnected",
    on: (event: string, handler: () => void) => (handlers[event] ??= new Set()).add(handler),
    off: (event: string, handler: () => void) => handlers[event]?.delete(handler),
    invoke: () => Promise.resolve(),
  }),
}));

vi.mock("../../services/auth", () => ({
  chatScheduleService,
  repositoryService: { getEvery: () => Promise.resolve([]) },
  aiProviderService: { getAll: () => Promise.resolve([]) },
}));

import Schedules from "./Schedules";

const schedule = (enabled: boolean): ChatSchedule => ({
  id: "s1",
  name: "Weekly retro",
  prompt: "Review the week.",
  aiTag: null,
  cronExpression: "0 8 * * 1",
  timeZone: "UTC",
  enabled,
  repositoryScope: "All",
  repositoryIds: [],
  continueSession: true,
  nextFireAt: null,
  lastFiring: null,
  latestChatSessionId: null,
});

const page = (enabled: boolean): ChatScheduleList => ({
  schedulerPaused: false,
  schedules: [schedule(enabled)],
});

afterEach(() => {
  cleanup();
  reads.length = 0;
  for (const k of Object.keys(handlers)) delete handlers[k];
  vi.clearAllMocks();
});

describe("The schedules list's reads", () => {
  test("a read that went out before a write and answers after it does not undo the write", async () => {
    render(
      <MemoryRouter>
        <Schedules />
      </MemoryRouter>,
    );
    await waitFor(() => expect(reads).toHaveLength(1));
    act(() => reads[0].resolve(page(true)));
    const toggle = (await screen.findByRole("checkbox", {
      name: "Enabled: Weekly retro",
    })) as HTMLInputElement;
    expect(toggle.checked).toBe(true);

    // A hint asks for a read, which is slow to answer.
    act(() => handlers.ChatSchedulesChanged?.forEach((h) => h()));
    await waitFor(() => expect(reads).toHaveLength(2));

    // The owner turns it off, and the server confirms.
    chatScheduleService.update.mockResolvedValue(schedule(false));
    fireEvent.click(toggle);
    await waitFor(() => expect(reads).toHaveLength(3));
    expect(toggle.checked).toBe(false);

    // The read from before the write answers with the schedule still on.
    await act(async () => reads[1].resolve(page(true)));
    expect(toggle.checked).toBe(false);

    await act(async () => reads[2].resolve(page(false)));
    expect(toggle.checked).toBe(false);
  });
});
