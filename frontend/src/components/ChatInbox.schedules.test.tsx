import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import type { ChatSessionSummary } from "../types";

const { chatService } = vi.hoisted(() => ({ chatService: { listHistory: vi.fn() } }));

vi.mock("../hooks/useSignalR", async () => (await import("../test-support")).fakeSignalRModule());

vi.mock("../services/auth", () => ({ chatService }));

import { ChatInboxProvider, useChatInbox } from "./ChatInbox";
import { emitHubEvent, signalRHub } from "../test-support";

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

// A hub that never connects, so the inbox reads its history on mount and on a hint only.
beforeEach(() => signalRHub.setState("disconnected"));

afterEach(() => {
  cleanup();
  signalRHub.reset();
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
    emitHubEvent("ChatSchedulesChanged", { scheduleId: "s1" });
    expect(await screen.findByText("Monday retro")).toBeTruthy();

    chatService.listHistory.mockResolvedValue([chat(null)]);
    emitHubEvent("ChatSchedulesChanged", { scheduleId: "s1" });
    await waitFor(() => expect(screen.getByText("no schedule")).toBeTruthy());
  });
});
