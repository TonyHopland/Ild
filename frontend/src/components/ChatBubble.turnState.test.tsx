import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor, act } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import type { ChatMessage, ChatSession, ChatSessionSummary } from "../types";

// The turn the bubble believes is running outlives every request it makes: a
// send, a stop and a state read all resolve long after the click that started
// them, and each of them can land on a chat that has since moved on. These are
// the paths ChatBubble.test.tsx does not reach — a request that fails, and an
// answer that arrives for a chat the user has already left.
const { handlers, invoke, chatService, aiProviderService, getOpenLoopDocument } = vi.hoisted(
  () => ({
    handlers: {} as Record<string, (msg: { payload: unknown }) => void>,
    invoke: vi.fn(() => Promise.resolve()),
    chatService: {
      listHistory: vi.fn(),
      getById: vi.fn(),
      start: vi.fn(),
      sendMessage: vi.fn(),
      interrupt: vi.fn(),
      deleteOne: vi.fn(),
      deleteAll: vi.fn(),
    },
    aiProviderService: { getAll: vi.fn() },
    getOpenLoopDocument: vi.fn(),
  }),
);

vi.mock("../hooks/useSignalR", () => ({
  useSignalR: () => ({
    connectionState: "connected",
    on: (event: string, handler: (msg: { payload: unknown }) => void) => {
      handlers[event] = handler;
    },
    off: (event: string) => {
      delete handlers[event];
    },
    invoke,
  }),
}));

vi.mock("../services/auth", () => ({ chatService, aiProviderService }));
vi.mock("../utils/openLoopDocument", () => ({ getOpenLoopDocument }));
vi.mock("../services/chatSessionStore", () => ({ setCurrentChatSessionId: vi.fn() }));

import ChatBubble from "./ChatBubble";

function chatSession(partial: Partial<ChatSession> = {}): ChatSession {
  return {
    id: partial.id ?? "s1",
    name: partial.name ?? "Past chat",
    aiProviderId: "p1",
    providerType: "claude-code",
    tools: ["ild"],
    createdAt: "2026-01-01T00:00:00Z",
    messages: partial.messages ?? [],
    activeTurnId: partial.activeTurnId ?? null,
  };
}

function msg(partial: Partial<ChatMessage>): ChatMessage {
  return {
    id: partial.id ?? crypto.randomUUID(),
    role: partial.role ?? "assistant",
    content: partial.content ?? "",
    interrupted: false,
    sequence: partial.sequence ?? 0,
    createdAt: "2026-01-01T00:00:00Z",
  };
}

function summary(id: string, name: string): ChatSessionSummary {
  return { id, name, createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-02T00:00:00Z" };
}

function emit(event: string, payload: Record<string, unknown>) {
  act(() => {
    handlers[event]?.({ payload });
  });
}

/** Open the bubble on the chat list, with the given chats in the history. */
function openList(...chats: ChatSessionSummary[]) {
  chatService.listHistory.mockResolvedValue(chats);
  // The start form loads them behind the list; none of these tests start a chat.
  aiProviderService.getAll.mockResolvedValue([]);
  return render(
    <MemoryRouter initialEntries={["/"]}>
      <ChatBubble />
    </MemoryRouter>,
  );
}

/**
 * Resume a chat and wait for the join's own state read, so a test that swaps the
 * `getById` mock afterwards is swapping it for its own call and not for that one.
 */
async function openResumed(session: ChatSession) {
  openList(summary(session.id, session.name ?? "Past chat"));
  chatService.getById.mockResolvedValue(session);
  fireEvent.click(await screen.findByLabelText("Open chat"));
  fireEvent.click(await screen.findByText(session.name ?? "Past chat"));
  await screen.findByLabelText("Chat message");
  await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(2));
}

async function sendMessageText(text: string) {
  const input = await screen.findByLabelText("Chat message");
  fireEvent.change(input, { target: { value: text } });
  fireEvent.click(screen.getByText("Send"));
}

afterEach(() => {
  cleanup();
  for (const k of Object.keys(handlers)) delete handlers[k];
  vi.clearAllMocks();
  getOpenLoopDocument.mockReset();
  localStorage.clear();
});

describe("ChatBubble turn state", () => {
  test("a send that fails asks the server rather than declaring the chat idle", async () => {
    await openResumed(chatSession());
    chatService.sendMessage.mockRejectedValue(new Error("Network error."));
    // The POST failed on the way back: the runner already has the message and is
    // working on it, so taking the stop button away would strand a live turn.
    chatService.getById.mockResolvedValue(chatSession({ activeTurnId: "t9" }));

    await sendMessageText("did this arrive?");

    expect(await screen.findByText("Network error.")).toBeTruthy();
    await waitFor(() => expect(screen.getByLabelText("Stop")).toBeTruthy());
    expect(screen.getByRole("status")).toBeTruthy();
  });

  test("a send that fails with no answer from the server clears the turn it put up", async () => {
    await openResumed(chatSession());
    chatService.sendMessage.mockRejectedValue(new Error("Network error."));
    chatService.getById.mockRejectedValue(new Error("Network error."));

    await sendMessageText("did this arrive?");

    expect(await screen.findByText("Network error.")).toBeTruthy();
    await waitFor(() => expect(screen.queryByLabelText("Stop")).toBeNull());
    expect(screen.queryByRole("status")).toBeNull();
  });

  test("a turn announced while a failed send unwinds survives it", async () => {
    await openResumed(chatSession());

    let failSend!: (err: Error) => void;
    chatService.sendMessage.mockReturnValue(
      new Promise<void>((_, reject) => {
        failSend = reject;
      }),
    );
    let failRead!: (err: Error) => void;
    chatService.getById.mockReturnValue(
      new Promise<ChatSession>((_, reject) => {
        failRead = reject;
      }),
    );

    await sendMessageText("did this arrive?");
    await act(async () => {
      failSend(new Error("Network error."));
    });
    await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(3));

    // The runner did get the message: it announces the turn while the client is
    // still unwinding the send that failed.
    emit("ChatTurnStarted", { chatSessionId: "s1", turnId: "t5" });
    await act(async () => {
      failRead(new Error("Network error."));
    });

    expect(screen.getByLabelText("Stop")).toBeTruthy();
    expect(screen.getByRole("status")).toBeTruthy();

    // And it is that turn's completion that ends it, not the failed send's.
    emit("ChatTurnCompleted", { chatSessionId: "s1", turnId: "t5", interrupted: false });
    expect(screen.queryByLabelText("Stop")).toBeNull();
  });

  test("a state read answered after the user left never lands on the chat they moved to", async () => {
    chatService.interrupt.mockResolvedValue(undefined);
    chatService.sendMessage.mockResolvedValue(undefined);
    openList(summary("s1", "First chat"), summary("s2", "Other chat"));
    chatService.getById.mockResolvedValue(chatSession({ id: "s1", name: "First chat" }));

    fireEvent.click(await screen.findByLabelText("Open chat"));
    fireEvent.click(await screen.findByText("First chat"));
    await screen.findByLabelText("Chat message");

    await sendMessageText("long task");
    emit("ChatTurnStarted", { chatSessionId: "s1", turnId: "t1" });

    // The stop's own read hangs while the server is still cancelling.
    let answerFirst!: (session: ChatSession) => void;
    chatService.getById.mockImplementation((id: string) =>
      id === "s1"
        ? new Promise<ChatSession>((resolve) => {
            answerFirst = resolve;
          })
        : Promise.resolve(chatSession({ id: "s2", name: "Other chat" })),
    );

    fireEvent.click(await screen.findByLabelText("Stop"));
    await waitFor(() => expect(chatService.interrupt).toHaveBeenCalledWith("s1"));

    fireEvent.click(screen.getByText("← Back"));
    fireEvent.click(await screen.findByText("Other chat"));
    await screen.findByLabelText("Chat message");

    // The first chat finally answers, and it is still busy — but the bubble is
    // looking at another chat now, so neither its turn nor its transcript may
    // reach the one on screen.
    await act(async () => {
      answerFirst(
        chatSession({
          id: "s1",
          name: "First chat",
          activeTurnId: "t1",
          messages: [msg({ id: "m1", content: "belongs to the first chat", sequence: 0 })],
        }),
      );
    });

    expect(screen.queryByLabelText("Stop")).toBeNull();
    expect(screen.queryByRole("status")).toBeNull();
    expect(screen.queryByText("belongs to the first chat")).toBeNull();
  });
});
