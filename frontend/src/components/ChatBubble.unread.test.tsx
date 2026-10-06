import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor, act, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import type {
  ChatMessage,
  ChatMessageAppendedPayload,
  ChatSession,
  ChatSessionSummary,
  ChatUnreadChangedPayload,
} from "../types";

const {
  handlers,
  connection,
  invoke,
  chatService,
  aiProviderService,
  workItemService,
  getOpenLoopDocument,
  setCurrentChatSessionId,
} = vi.hoisted(() => ({
  handlers: {} as Record<string, (msg: { payload: unknown }) => void>,
  connection: {
    state: "connected" as "disconnected" | "connecting" | "connected" | "reconnecting",
  },
  invoke: vi.fn((..._args: unknown[]) => Promise.resolve()),
  chatService: {
    listHistory: vi.fn(),
    getById: vi.fn(),
    start: vi.fn(),
    sendMessage: vi.fn(),
    interrupt: vi.fn(),
    deleteOne: vi.fn(),
    deleteAll: vi.fn(),
    markRead: vi.fn(),
  },
  aiProviderService: { getAll: vi.fn(() => Promise.resolve([])) },
  workItemService: { listEditProposalsFor: vi.fn(() => Promise.resolve([])) },
  getOpenLoopDocument: vi.fn(),
  setCurrentChatSessionId: vi.fn(),
}));

vi.mock("../hooks/useSignalR", () => ({
  useSignalR: () => ({
    connectionState: connection.state,
    on: (event: string, handler: (msg: { payload: unknown }) => void) => {
      handlers[event] = handler;
    },
    off: (event: string) => {
      delete handlers[event];
    },
    invoke,
  }),
}));

vi.mock("../services/auth", () => ({ chatService, aiProviderService, workItemService }));
vi.mock("../utils/openLoopDocument", () => ({ getOpenLoopDocument }));
vi.mock("../services/chatSessionStore", () => ({ setCurrentChatSessionId }));

import ChatBubble from "./ChatBubble";
import { setChatEnabled } from "../hooks/useChatEnabled";
import { openChatFromList, openChatList, startNewChat } from "../test-support";

interface Deferred<T> {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (reason: unknown) => void;
}

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

// A stand-in for the server: what GET /chat/history answers right now, and a
// mark-read that clears the chat's unread flag there. A test changes `server` to
// say what the server has come to know, and holds a history read to answer it
// later with a snapshot of its choosing.
let server: ChatSessionSummary[] = [];
let heldReads: Deferred<ChatSessionSummary[]>[] = [];
let readsToHold = 0;
const sessions: Record<string, ChatSession> = {};

function summary(id: string, name: string, hasUnread: boolean): ChatSessionSummary {
  return {
    id,
    name,
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-02T00:00:00Z",
    hasUnread,
  };
}

function msg(chat: string, sequence: number, role: "user" | "assistant"): ChatMessage {
  return {
    id: `${chat}-${sequence}`,
    role,
    content: `${role} message ${sequence}`,
    interrupted: false,
    sequence,
    createdAt: "2026-01-01T00:00:00Z",
  };
}

function chat(id: string, name: string, messages: ChatMessage[]): ChatSession {
  return {
    id,
    name,
    aiProviderId: "p1",
    providerType: "claude-code",
    tools: ["ild"],
    createdAt: "2026-01-01T00:00:00Z",
    messages,
    activeTurnId: null,
  };
}

/** The next `count` history reads stay in flight until the test answers them. */
function holdHistoryReads(count: number) {
  readsToHold += count;
}

function setUnread(id: string, hasUnread: boolean) {
  server = server.map((c) => (c.id === id ? { ...c, hasUnread } : c));
}

beforeEach(() => {
  chatService.listHistory.mockImplementation(() => {
    if (readsToHold > 0) {
      readsToHold -= 1;
      const held = deferred<ChatSessionSummary[]>();
      heldReads.push(held);
      return held.promise;
    }
    return Promise.resolve(server.map((c) => ({ ...c })));
  });
  chatService.getById.mockImplementation((id: string) => Promise.resolve(sessions[id]));
  chatService.markRead.mockImplementation((id: string) => {
    setUnread(id, false);
    return Promise.resolve();
  });
  chatService.deleteOne.mockImplementation((id: string) => {
    server = server.filter((c) => c.id !== id);
    return Promise.resolve();
  });
  chatService.deleteAll.mockImplementation(() => {
    server = [];
    return Promise.resolve();
  });
  chatService.sendMessage.mockResolvedValue("t1");
});

afterEach(() => {
  cleanup();
  for (const k of Object.keys(handlers)) delete handlers[k];
  for (const k of Object.keys(sessions)) delete sessions[k];
  connection.state = "connected";
  server = [];
  heldReads = [];
  readsToHold = 0;
  vi.clearAllMocks();
  localStorage.clear();
});

function bubble() {
  return (
    <MemoryRouter initialEntries={["/"]}>
      <ChatBubble />
    </MemoryRouter>
  );
}

/** Let every answer already given reach the view, and every effect it causes run. */
async function settle() {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
}

function emitAppended(payload: ChatMessageAppendedPayload) {
  act(() => {
    handlers.ChatMessageAppended?.({ payload });
  });
}

function emitUnreadChanged(payload: ChatUnreadChangedPayload) {
  act(() => {
    handlers.ChatUnreadChanged?.({ payload });
  });
}

function fab() {
  return screen.getByRole("button", { name: "Open chat" });
}

function fabDot() {
  return within(fab()).queryByRole("img", { name: "New messages" });
}

function row(name: string) {
  return screen.getByText(name).closest("li") as HTMLElement;
}

function rowDot(name: string) {
  return within(row(name)).queryByRole("img", { name: "New messages" });
}

async function openPanel() {
  fireEvent.click(await screen.findByRole("button", { name: "Open chat" }));
}

function closePanel() {
  fireEvent.click(screen.getByLabelText("Close chat"));
}

async function resume(name: string) {
  await openChatFromList(name);
  await screen.findByLabelText("Chat message");
}

/** Leave the open chat, then show the list again. */
async function back() {
  await startNewChat();
  await screen.findByText("Start chat");
  await openChatList();
}

async function send(text: string) {
  fireEvent.change(screen.getByLabelText("Chat message"), { target: { value: text } });
  fireEvent.click(screen.getByText("Send"));
  await waitFor(() => expect(chatService.sendMessage).toHaveBeenCalled());
  await settle();
}

describe("unread chat indicator", () => {
  test("after a reload the closed button and each unread row carry the dot, read rows none", async () => {
    server = [summary("a", "Alpha", true), summary("b", "Beta", false)];

    render(bubble());

    await waitFor(() => expect(fabDot()).not.toBeNull());
    // The dot adds to the button without renaming it.
    expect(fab().getAttribute("aria-label")).toBe("Open chat");
    expect(screen.queryAllByRole("status")).toHaveLength(0);

    await openPanel();
    await openChatList();
    await screen.findByText("Beta");
    expect(rowDot("Alpha")).not.toBeNull();
    expect(rowDot("Beta")).toBeNull();
    expect(screen.queryAllByRole("status")).toHaveLength(0);
  });

  test("send, close: the reply lights the button live, and reopening reads it and clears the dot", async () => {
    server = [summary("a", "Alpha", false)];
    sessions.a = chat("a", "Alpha", [msg("a", 0, "user"), msg("a", 1, "assistant")]);
    render(bubble());
    await openPanel();
    await resume("Alpha");

    await send("do the thing");
    emitAppended({ chatSessionId: "a", turnId: "t1", message: msg("a", 2, "user") });
    closePanel();

    // The reply is finalized while the panel is closed.
    emitAppended({ chatSessionId: "a", turnId: "t1", message: msg("a", 3, "assistant") });
    setUnread("a", true);
    emitUnreadChanged({ chatSessionId: "a" });

    await waitFor(() => expect(fabDot()).not.toBeNull());
    expect(chatService.markRead).not.toHaveBeenCalledWith("a", 3);

    await openPanel();
    // Still the chat that was open, now read up to the reply.
    expect(within(screen.getByRole("dialog")).getByText("Alpha")).toBeTruthy();
    expect(screen.getByLabelText("Chat message")).toBeTruthy();
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("a", 3));
    await settle();

    closePanel();
    await waitFor(() => expect(fabDot()).toBeNull());
  });

  test("a reply seen in the open chat is marked read, and a history read taken before that settled cannot bring the dot back", async () => {
    server = [summary("a", "Alpha", false)];
    sessions.a = chat("a", "Alpha", [msg("a", 0, "user")]);
    render(bubble());
    await openPanel();
    await resume("Alpha");
    await settle();

    const marking = deferred<void>();
    chatService.markRead.mockImplementationOnce(() => marking.promise);
    holdHistoryReads(2);

    // The reply lands and its hint's history read goes out while the reply is unread.
    emitAppended({ chatSessionId: "a", turnId: "t1", message: msg("a", 1, "assistant") });
    setUnread("a", true);
    emitUnreadChanged({ chatSessionId: "a" });
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("a", 1));
    await waitFor(() => expect(heldReads).toHaveLength(1));

    // The mark-read settles; whatever history read follows it stays in flight.
    setUnread("a", false);
    await act(async () => marking.resolve());
    await waitFor(() => expect(heldReads).toHaveLength(2));

    // The snapshot from before the mark-read settled answers first, still saying unread.
    await act(async () => heldReads[0].resolve([summary("a", "Alpha", true)]));
    await settle();
    closePanel();
    expect(fabDot()).toBeNull();

    await act(async () => heldReads[1].resolve([summary("a", "Alpha", false)]));
    await settle();
    expect(fabDot()).toBeNull();
  });

  test("a reply for a chat left for the list or for another chat lights that chat's row and the button, never the other chat", async () => {
    server = [summary("a", "Alpha", false), summary("b", "Beta", false)];
    sessions.a = chat("a", "Alpha", [msg("a", 0, "user"), msg("a", 1, "assistant")]);
    sessions.b = chat("b", "Beta", [msg("b", 0, "user"), msg("b", 1, "assistant")]);
    render(bubble());
    await openPanel();
    await resume("Alpha");
    await send("question for A");
    emitAppended({ chatSessionId: "a", turnId: "t1", message: msg("a", 2, "user") });
    await back();

    // A's reply lands while the list is on screen.
    setUnread("a", true);
    emitUnreadChanged({ chatSessionId: "a" });
    await waitFor(() => expect(rowDot("Alpha")).not.toBeNull());
    expect(rowDot("Beta")).toBeNull();

    // Opening B reads B, not A.
    await resume("Beta");
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("b", 1));
    await settle();
    closePanel();
    await waitFor(() => expect(fabDot()).not.toBeNull());
    expect(chatService.markRead.mock.calls.filter(([id, seq]) => id === "a" && seq >= 3)).toEqual(
      [],
    );

    await openPanel();
    await back();
    await waitFor(() => expect(rowDot("Alpha")).not.toBeNull());
    expect(rowDot("Beta")).toBeNull();
  });

  test("opening an unread chat from the list reads it up to its newest message, and other unread chats keep their dots", async () => {
    server = [summary("a", "Alpha", true), summary("b", "Beta", true)];
    sessions.a = chat("a", "Alpha", [
      msg("a", 0, "user"),
      msg("a", 1, "assistant"),
      msg("a", 2, "user"),
      msg("a", 3, "assistant"),
    ]);
    sessions.b = chat("b", "Beta", [msg("b", 4, "user"), msg("b", 5, "assistant")]);
    render(bubble());
    await openPanel();
    await openChatList();
    await screen.findByText("Beta");
    expect(rowDot("Alpha")).not.toBeNull();
    expect(rowDot("Beta")).not.toBeNull();

    await resume("Alpha");
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("a", 3));
    await settle();
    await back();
    await waitFor(() => expect(rowDot("Alpha")).toBeNull());
    expect(rowDot("Beta")).not.toBeNull();
    closePanel();
    expect(fabDot()).not.toBeNull();

    await openPanel();
    await resume("Beta");
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("b", 5));
    await settle();
    closePanel();
    await waitFor(() => expect(fabDot()).toBeNull());
  });

  test("a failed mark-read shows no error, leaves the chat unread, touches no other chat, and is tried again on the next open", async () => {
    server = [summary("a", "Alpha", true), summary("b", "Beta", true)];
    sessions.a = chat("a", "Alpha", [msg("a", 0, "user"), msg("a", 1, "assistant")]);
    sessions.b = chat("b", "Beta", [msg("b", 0, "user"), msg("b", 1, "assistant")]);
    const markingA = deferred<void>();
    chatService.markRead.mockImplementationOnce(() => markingA.promise);
    render(bubble());
    await openPanel();

    await resume("Alpha");
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("a", 1));
    await back();
    await resume("Beta");
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("b", 1));
    await settle();
    await back();
    await waitFor(() => expect(rowDot("Beta")).toBeNull());

    await act(async () => markingA.reject(new Error("mark-read failed")));
    await settle();

    expect(screen.queryByText("mark-read failed")).toBeNull();
    expect(rowDot("Alpha")).not.toBeNull();
    expect(rowDot("Beta")).toBeNull();

    await resume("Alpha");
    await waitFor(() =>
      expect(chatService.markRead.mock.calls.filter(([id]) => id === "a")).toEqual([
        ["a", 1],
        ["a", 1],
      ]),
    );
    await settle();
    await back();
    await waitFor(() => expect(rowDot("Alpha")).toBeNull());
  });

  test("a reply that lands while chat is hidden in settings is read only once the chat is shown again", async () => {
    server = [summary("a", "Alpha", false)];
    sessions.a = chat("a", "Alpha", [msg("a", 0, "user")]);
    render(bubble());
    await openPanel();
    await resume("Alpha");
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("a", 0));

    act(() => setChatEnabled(false));
    emitAppended({ chatSessionId: "a", turnId: "t1", message: msg("a", 1, "assistant") });
    await settle();
    expect(chatService.markRead).not.toHaveBeenCalledWith("a", 1);

    act(() => setChatEnabled(true));
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("a", 1));
  });

  test("every (re)connect rejoins the inbox and re-reads history, recovering a hint lost while away", async () => {
    server = [summary("a", "Alpha", false)];
    const view = render(bubble());
    await waitFor(() => expect(invoke).toHaveBeenCalledWith("SubscribeToChatInbox"));
    await settle();
    expect(fabDot()).toBeNull();

    connection.state = "reconnecting";
    act(() => view.rerender(bubble()));
    // The reply lands, and its hint is lost, while the connection is down.
    setUnread("a", true);
    connection.state = "connected";
    act(() => view.rerender(bubble()));

    await waitFor(() =>
      expect(
        invoke.mock.calls.filter(([method]) => method === "SubscribeToChatInbox"),
      ).toHaveLength(2),
    );
    await waitFor(() => expect(fabDot()).not.toBeNull());
  });

  test("a history read in flight when a chat's delete settles never brings the chat or its dot back", async () => {
    server = [summary("a", "Alpha", true), summary("b", "Beta", false)];
    render(bubble());
    await openPanel();
    await openChatList();
    await screen.findByText("Alpha");

    holdHistoryReads(1);
    emitUnreadChanged({ chatSessionId: "a" });
    await waitFor(() => expect(heldReads).toHaveLength(1));
    fireEvent.click(screen.getByLabelText("Delete chat Alpha"));
    await waitFor(() => expect(screen.queryByText("Alpha")).toBeNull());

    await act(async () =>
      heldReads[0].resolve([summary("a", "Alpha", true), summary("b", "Beta", false)]),
    );
    await settle();

    expect(screen.queryByText("Alpha")).toBeNull();
    expect(screen.getByText("Beta")).toBeTruthy();
    closePanel();
    expect(fabDot()).toBeNull();
  });

  test("a history read in flight when delete all settles never brings any chat or dot back", async () => {
    server = [summary("a", "Alpha", true), summary("b", "Beta", true)];
    render(bubble());
    await openPanel();
    await openChatList();
    await screen.findByText("Alpha");

    holdHistoryReads(1);
    emitUnreadChanged({ chatSessionId: "b" });
    await waitFor(() => expect(heldReads).toHaveLength(1));
    fireEvent.click(screen.getByText("Delete all"));
    fireEvent.click(
      within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Delete all" }),
    );
    await waitFor(() => expect(screen.queryByText("Alpha")).toBeNull());

    await act(async () =>
      heldReads[0].resolve([summary("a", "Alpha", true), summary("b", "Beta", true)]),
    );
    await settle();

    expect(screen.queryByText("Alpha")).toBeNull();
    expect(screen.queryByText("Beta")).toBeNull();
    closePanel();
    expect(fabDot()).toBeNull();
  });

  test("a hint about another chat in flight when a delete settles still lights that chat", async () => {
    server = [summary("a", "Alpha", false), summary("b", "Beta", false)];
    render(bubble());
    await openPanel();
    await openChatList();
    await screen.findByText("Alpha");

    // B's reply lands and its hint's read goes out before A's delete settles.
    holdHistoryReads(1);
    setUnread("b", true);
    emitUnreadChanged({ chatSessionId: "b" });
    await waitFor(() => expect(heldReads).toHaveLength(1));
    fireEvent.click(screen.getByLabelText("Delete chat Alpha"));
    await waitFor(() => expect(screen.queryByText("Alpha")).toBeNull());

    await act(async () =>
      heldReads[0].resolve([summary("a", "Alpha", false), summary("b", "Beta", true)]),
    );

    await waitFor(() => expect(rowDot("Beta")).not.toBeNull());
    expect(screen.queryByText("Alpha")).toBeNull();
    closePanel();
    expect(fabDot()).not.toBeNull();
  });
});
