import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import type { ChatSessionSummary } from "../types";

const { handlers, chatService } = vi.hoisted(() => ({
  handlers: {} as Record<string, Set<(msg: { payload: unknown }) => void>>,
  chatService: { listHistory: vi.fn() },
}));

vi.mock("../hooks/useSignalR", () => ({
  useSignalR: () => ({
    connectionState: "disconnected",
    on: (event: string, handler: (msg: { payload: unknown }) => void) =>
      (handlers[event] ??= new Set()).add(handler),
    off: (event: string, handler: (msg: { payload: unknown }) => void) =>
      handlers[event]?.delete(handler),
    invoke: () => Promise.resolve(),
  }),
}));

vi.mock("../services/auth", () => ({ chatService }));

import { ChatInboxProvider, useChatInbox } from "./ChatInbox";

const chat = (scheduleName: string | null): ChatSessionSummary => ({
  id: "c1",
  name: "Retro chat",
  createdAt: "2026-07-06T06:00:00Z",
  updatedAt: "2026-07-06T06:03:00Z",
  scheduleId: scheduleName === null ? null : "s1",
  scheduleName,
});

function Marks() {
  const { history } = useChatInbox();
  return (
    <ul>
      {history.map((c) => (
        <li key={c.id}>{c.scheduleName ?? "no schedule"}</li>
      ))}
    </ul>
  );
}

afterEach(() => {
  cleanup();
  for (const k of Object.keys(handlers)) delete handlers[k];
  vi.clearAllMocks();
});

describe("The chat list's schedule marks", () => {
  test("follow a schedule that is renamed and then deleted", async () => {
    chatService.listHistory.mockResolvedValue([chat("Weekly retro")]);
    render(
      <ChatInboxProvider>
        <Marks />
      </ChatInboxProvider>,
    );
    expect(await screen.findByText("Weekly retro")).toBeTruthy();

    chatService.listHistory.mockResolvedValue([chat("Monday retro")]);
    act(() => handlers.ChatSchedulesChanged?.forEach((h) => h({ payload: { scheduleId: "s1" } })));
    expect(await screen.findByText("Monday retro")).toBeTruthy();

    chatService.listHistory.mockResolvedValue([chat(null)]);
    act(() => handlers.ChatSchedulesChanged?.forEach((h) => h({ payload: { scheduleId: "s1" } })));
    await waitFor(() => expect(screen.getByText("no schedule")).toBeTruthy());
  });
});
