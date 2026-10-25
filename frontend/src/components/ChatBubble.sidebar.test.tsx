import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor, act, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import type { ChatMessage, ChatSession, ChatSessionSummary } from "../types";
import {
  PANEL_POSITION_KEY,
  PANEL_SIZE_KEY,
  SIDEBAR_OPEN_KEY,
  saveSidebarOpen,
} from "./chatPlacement";

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
    deleteMany: vi.fn(),
    markRead: vi.fn(),
    rename: vi.fn(),
    setFavorite: vi.fn(),
    searchChats: vi.fn(),
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
import { ChatInboxProvider } from "./ChatInbox";
import { showChatActions } from "../test-support";
import { setPreference, getPreference, seedPreferences } from "../utils/userPreferences";
import { WorkItemHubProvider } from "../hooks/useWorkItemHub";

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

// A stand-in for the server: what GET /chat/history answers right now (in its
// last-activity order), and the writes that change it. A test can hold history
// reads and answer them later with a snapshot of its choosing.
let server: ChatSessionSummary[] = [];
let heldReads: Deferred<ChatSessionSummary[]>[] = [];
let readsToHold = 0;
let searches: { q: string; answer: Deferred<string[]> }[] = [];
const sessions: Record<string, ChatSession> = {};

function summary(
  id: string,
  name: string | null,
  extra: Partial<ChatSessionSummary> = {},
): ChatSessionSummary {
  return {
    id,
    name,
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-02T00:00:00Z",
    hasUnread: false,
    ...extra,
  };
}

function msg(chat: string, sequence: number, role: "user" | "assistant"): ChatMessage {
  return {
    id: `${chat}-${sequence}`,
    role,
    content: `${role} message ${sequence} of ${chat}`,
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
  chatService.deleteMany.mockImplementation((ids: string[]) => {
    server = server.filter((c) => !ids.includes(c.id));
    return Promise.resolve();
  });
  chatService.rename.mockImplementation((id: string, name: string) => {
    server = server.map((c) => (c.id === id ? { ...c, name } : c));
    return Promise.resolve();
  });
  chatService.setFavorite.mockImplementation((id: string, favorite: boolean) => {
    server = server.map((c) => (c.id === id ? { ...c, isFavorite: favorite } : c));
    return Promise.resolve();
  });
  chatService.searchChats.mockImplementation((q: string) => {
    const answer = deferred<string[]>();
    searches.push({ q, answer });
    return answer.promise;
  });
  chatService.sendMessage.mockResolvedValue("t1");
});

afterEach(() => {
  vi.useRealTimers();
  cleanup();
  for (const k of Object.keys(handlers)) delete handlers[k];
  for (const k of Object.keys(sessions)) delete sessions[k];
  connection.state = "connected";
  server = [];
  heldReads = [];
  readsToHold = 0;
  searches = [];
  vi.clearAllMocks();
  localStorage.clear();
  window.innerWidth = 1024;
  window.innerHeight = 768;
});

/** A panel wide enough for the list and the chat side by side (jsdom's viewport is 1024×768). */
function widePanel() {
  setPreference(PANEL_SIZE_KEY, JSON.stringify({ width: 800, height: 600 }));
}
// Without a stored size the panel takes its 384px default: narrow.

function bubble() {
  return (
    <MemoryRouter initialEntries={["/"]}>
      <ChatInboxProvider>
        <ChatBubble />
      </ChatInboxProvider>
    </MemoryRouter>
  );
}

async function settle() {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
}

/** Lets answered promises reach the view without running any timer (fake timers safe). */
async function flush() {
  await act(async () => {
    for (let i = 0; i < 10; i++) await Promise.resolve();
  });
}

async function openPanel() {
  fireEvent.click(await screen.findByRole("button", { name: "Open chat" }));
  await waitFor(() => expect(screen.queryByText("Loading…")).toBeNull());
}

function toggle() {
  return screen.getByRole("button", { name: "Chat list" });
}

function querySidebar() {
  return screen.queryByRole("complementary", { name: "Chats" });
}

function sidebar() {
  return screen.getByRole("complementary", { name: "Chats" });
}

function row(name: string) {
  return within(sidebar()).getByText(name).closest("li") as HTMLElement;
}

/** The names of the listed chats, top to bottom. */
function rowOrder(names: string[]) {
  return within(sidebar())
    .queryAllByRole("listitem")
    .map((li) => names.find((n) => li.textContent?.includes(n)))
    .filter((n): n is string => n !== undefined);
}

function star(name: string) {
  showChatActions(name);
  return within(sidebar()).getByRole("button", { name: `Favorite chat ${name}` });
}

function header() {
  return toggle().closest(".chat-panel-header") as HTMLElement;
}

function badge(count: number) {
  return within(header()).queryByText(String(count));
}

async function pick(name: string) {
  fireEvent.click(within(sidebar()).getByText(name));
  await screen.findByLabelText("Chat message");
}

function dragResizeHandle(dx: number) {
  const handle = screen.getByLabelText("Resize chat");
  fireEvent.pointerDown(handle, { clientX: 0, clientY: 0 });
  fireEvent.pointerMove(window, { clientX: dx, clientY: 0 });
  fireEvent.pointerUp(window, { clientX: dx, clientY: 0 });
}

function resizeWindow(width: number) {
  act(() => {
    window.innerWidth = width;
    window.dispatchEvent(new Event("resize"));
  });
}

function emitAppended(chatSessionId: string, message: ChatMessage) {
  act(() => {
    handlers.ChatMessageAppended?.({ payload: { chatSessionId, turnId: "t1", message } });
  });
}

function emitUnreadChanged(chatSessionId: string) {
  act(() => {
    handlers.ChatUnreadChanged?.({ payload: { chatSessionId } });
  });
}

describe("chat sidebar layout", () => {
  test("wide: the list sits beside a usable chat, the toggle hides it completely, and no Back button remains", async () => {
    widePanel();
    server = [summary("a", "Alpha"), summary("b", "Beta")];
    sessions.a = chat("a", "Alpha", [msg("a", 0, "user"), msg("a", 1, "assistant")]);
    render(bubble(), { wrapper: WorkItemHubProvider });
    await openPanel();

    // Open by default on a wide panel, beside the start form.
    expect(toggle().getAttribute("aria-expanded")).toBe("true");
    expect(querySidebar()).not.toBeNull();
    expect(screen.getByText("Start chat")).toBeTruthy();

    await pick("Alpha");
    // Picking a row keeps the list open, and the chat beside it works.
    expect(querySidebar()).not.toBeNull();
    expect(screen.getByText("assistant message 1 of a")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Rename" })).toBeNull();
    expect(screen.queryByText(/Back/)).toBeNull();
    fireEvent.change(screen.getByLabelText("Chat message"), { target: { value: "hello" } });
    fireEvent.click(screen.getByText("Send"));
    await waitFor(() =>
      expect(chatService.sendMessage).toHaveBeenCalledWith("a", "hello", null, null),
    );
    await settle();
    act(() => {
      handlers.ChatTurnStarted?.({ payload: { chatSessionId: "a", turnId: "t1" } });
    });
    expect(screen.getByRole("button", { name: "Stop" })).toBeTruthy();

    fireEvent.click(toggle());
    expect(toggle().getAttribute("aria-expanded")).toBe("false");
    expect(querySidebar()).toBeNull();
    expect(screen.queryByLabelText("Search chats")).toBeNull();
    expect(screen.queryByRole("button", { name: "New chat" })).toBeNull();
    expect(screen.getByLabelText("Chat message")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Stop" })).toBeTruthy();

    fireEvent.click(toggle());
    expect(querySidebar()).not.toBeNull();
    fireEvent.click(within(sidebar()).getByRole("button", { name: "New chat" }));
    expect(await screen.findByText("Start chat")).toBeTruthy();
    expect(querySidebar()).not.toBeNull();
  });

  test("narrow: the open list fills the panel; a row or New chat closes it and shows that chat or the start form", async () => {
    server = [summary("a", "Alpha"), summary("b", "Beta")];
    sessions.a = chat("a", "Alpha", [msg("a", 0, "user"), msg("a", 1, "assistant")]);
    render(bubble(), { wrapper: WorkItemHubProvider });
    await openPanel();

    // Closed by default on a narrow panel: the start form is shown, no list.
    expect(toggle().getAttribute("aria-expanded")).toBe("false");
    expect(querySidebar()).toBeNull();
    expect(screen.getByText("Start chat")).toBeTruthy();

    fireEvent.click(toggle());
    expect(querySidebar()).not.toBeNull();
    expect(screen.queryByText("Start chat")).toBeNull();

    await pick("Alpha");
    expect(querySidebar()).toBeNull();
    expect(toggle().getAttribute("aria-expanded")).toBe("false");
    expect(screen.getByText("assistant message 1 of a")).toBeTruthy();

    fireEvent.click(toggle());
    expect(querySidebar()).not.toBeNull();
    expect(screen.queryByLabelText("Chat message")).toBeNull();
    expect(screen.queryByText("assistant message 1 of a")).toBeNull();

    // New chat leaves the open chat the way Back did: nothing deleted, group left.
    fireEvent.click(within(sidebar()).getByRole("button", { name: "New chat" }));
    expect(await screen.findByText("Start chat")).toBeTruthy();
    expect(querySidebar()).toBeNull();
    expect(screen.queryByLabelText("Chat message")).toBeNull();
    expect(setCurrentChatSessionId).toHaveBeenLastCalledWith(null);
    expect(invoke).toHaveBeenCalledWith("UnsubscribeFromChat", "a");
    expect(chatService.deleteOne).not.toHaveBeenCalled();
    expect(chatService.deleteMany).not.toHaveBeenCalled();
  });

  test("pressing and dragging the toggle does not move the panel", async () => {
    render(bubble(), { wrapper: WorkItemHubProvider });
    await openPanel();
    const panel = screen.getByRole("dialog", { name: "AI chat" });
    const left = panel.style.left;
    const top = panel.style.top;

    fireEvent.pointerDown(toggle(), { clientX: 200, clientY: 200 });
    fireEvent.pointerMove(window, { clientX: 50, clientY: 50 });
    fireEvent.pointerUp(window, { clientX: 50, clientY: 50 });

    expect(panel.style.left).toBe(left);
    expect(panel.style.top).toBe(top);
    expect(getPreference(PANEL_POSITION_KEY)).toBeUndefined();
  });

  const DEFAULT_CASES: { name: string; wide: boolean; stored: () => void; open: boolean }[] = [
    { name: "nothing stored, wide", wide: true, stored: () => {}, open: true },
    { name: "nothing stored, narrow", wide: false, stored: () => {}, open: false },
    {
      name: "garbage stored, wide",
      wide: true,
      stored: () => setPreference(SIDEBAR_OPEN_KEY, "{not json"),
      open: true,
    },
    {
      name: "garbage stored, narrow",
      wide: false,
      stored: () => setPreference(SIDEBAR_OPEN_KEY, '"yes"'),
      open: false,
    },
    { name: "closed stored, wide", wide: true, stored: () => saveSidebarOpen(false), open: false },
    { name: "open stored, narrow", wide: false, stored: () => saveSidebarOpen(true), open: true },
  ];

  test.each(DEFAULT_CASES)("initial state: $name", async ({ wide, stored, open }) => {
    if (wide) widePanel();
    stored();
    server = [summary("a", "Alpha")];
    render(bubble(), { wrapper: WorkItemHubProvider });
    await openPanel();
    expect(toggle().getAttribute("aria-expanded")).toBe(String(open));
    expect(querySidebar() !== null).toBe(open);
  });

  test("the open/closed state survives a reload, including the narrow auto-close", async () => {
    widePanel();
    server = [summary("a", "Alpha")];
    sessions.a = chat("a", "Alpha", [msg("a", 0, "user")]);
    render(bubble(), { wrapper: WorkItemHubProvider });
    await openPanel();
    fireEvent.click(toggle());
    expect(querySidebar()).toBeNull();

    cleanup();
    render(bubble(), { wrapper: WorkItemHubProvider });
    await openPanel();
    expect(toggle().getAttribute("aria-expanded")).toBe("false");
    expect(querySidebar()).toBeNull();

    // Narrow and stored open: picking a chat closes it, and that is stored too.
    cleanup();
    // Back to the default (narrow) panel size.
    seedPreferences();
    saveSidebarOpen(true);
    render(bubble(), { wrapper: WorkItemHubProvider });
    await openPanel();
    expect(querySidebar()).not.toBeNull();
    await pick("Alpha");
    expect(querySidebar()).toBeNull();

    cleanup();
    render(bubble(), { wrapper: WorkItemHubProvider });
    await openPanel();
    expect(toggle().getAttribute("aria-expanded")).toBe("false");
  });

  test("resizing across the breakpoint switches presentation and keeps the open chat, its turn and the typed draft", async () => {
    widePanel();
    server = [summary("a", "Alpha"), summary("b", "Beta")];
    sessions.a = chat("a", "Alpha", [msg("a", 0, "user"), msg("a", 1, "assistant")]);
    render(bubble(), { wrapper: WorkItemHubProvider });
    await openPanel();
    await pick("Alpha");
    // The open reads the chat, and its join reads it again; neither is a reload.
    await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(2));
    await settle();
    const reads = chatService.getById.mock.calls.length;
    act(() => {
      handlers.ChatTurnStarted?.({ payload: { chatSessionId: "a", turnId: "t9" } });
    });
    fireEvent.change(screen.getByLabelText("Chat message"), { target: { value: "half typed" } });

    // The window shrinks, and the panel with it: the open list now fills the panel.
    resizeWindow(500);
    expect(querySidebar()).not.toBeNull();
    expect(screen.queryByLabelText("Chat message")).toBeNull();

    // Room again, and the panel dragged wider: side by side, same chat as before.
    resizeWindow(1024);
    dragResizeHandle(300);
    expect(querySidebar()).not.toBeNull();
    expect((screen.getByLabelText("Chat message") as HTMLInputElement).value).toBe("half typed");
    expect(screen.getByText("assistant message 1 of a")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Stop" })).toBeTruthy();
    expect(chatService.getById).toHaveBeenCalledTimes(reads);
    expect(setCurrentChatSessionId).toHaveBeenLastCalledWith("a");
  });
});

describe("chat sidebar list", () => {
  test("rows: starred first then the rest, each in history order; name or Untitled chat; relative time with the full date; the open chat alone is current", async () => {
    widePanel();
    const fiveMinAgo = new Date(Date.now() - 5 * 60_000).toISOString();
    const threeHoursAgo = new Date(Date.now() - 3 * 3_600_000).toISOString();
    server = [
      summary("a", "Alpha", { updatedAt: fiveMinAgo }),
      summary("b", "Beta", { isFavorite: true }),
      summary("c", null, { createdAt: threeHoursAgo, updatedAt: null }),
      summary("d", "Delta", { isFavorite: true }),
    ];
    sessions.a = chat("a", "Alpha", [msg("a", 0, "user")]);
    sessions.b = chat("b", "Beta", [msg("b", 0, "user")]);
    render(bubble(), { wrapper: WorkItemHubProvider });
    await openPanel();

    const names = ["Alpha", "Beta", "Untitled chat", "Delta"];
    expect(rowOrder(names)).toEqual(["Beta", "Delta", "Alpha", "Untitled chat"]);

    const fresh = within(row("Alpha")).getByText("5 min ago");
    expect(fresh.closest("[title]")?.getAttribute("title")).toBe(
      new Date(fiveMinAgo).toLocaleString(),
    );
    expect(within(row("Untitled chat")).getByText("3 h ago")).toBeTruthy();

    expect(sidebar().querySelectorAll('[aria-current="true"]')).toHaveLength(0);
    await pick("Alpha");
    await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(2));
    await settle();
    const reads = chatService.getById.mock.calls.length;
    let current = sidebar().querySelectorAll('[aria-current="true"]');
    expect(current).toHaveLength(1);
    expect(row("Alpha").contains(current[0])).toBe(true);

    // Clicking the open chat's row again neither re-reads nor resets it.
    fireEvent.change(screen.getByLabelText("Chat message"), { target: { value: "draft" } });
    fireEvent.click(within(sidebar()).getByText("Alpha"));
    await settle();
    expect(chatService.getById).toHaveBeenCalledTimes(reads);
    expect((screen.getByLabelText("Chat message") as HTMLInputElement).value).toBe("draft");

    await pick("Beta");
    await waitFor(() => expect(screen.getByText("user message 0 of b")).toBeTruthy());
    current = sidebar().querySelectorAll('[aria-current="true"]');
    expect(current).toHaveLength(1);
    expect(row("Beta").contains(current[0])).toBe(true);
  });

  test("rename, delete and bulk delete work from the list while a chat is open; deleting the open chat leaves it", async () => {
    widePanel();
    server = [summary("a", "Alpha"), summary("b", "Beta"), summary("c", "Gamma")];
    sessions.a = chat("a", "Alpha", [msg("a", 0, "user")]);
    render(bubble(), { wrapper: WorkItemHubProvider });
    await openPanel();
    await pick("Alpha");

    showChatActions("Beta");
    fireEvent.click(within(sidebar()).getByRole("button", { name: "Rename chat Beta" }));
    fireEvent.change(within(sidebar()).getByLabelText("Chat name"), {
      target: { value: "Bravo" },
    });
    fireEvent.click(within(sidebar()).getByRole("button", { name: "Save" }));
    await waitFor(() => expect(within(sidebar()).getByText("Bravo")).toBeTruthy());
    expect(chatService.rename).toHaveBeenCalledWith("b", "Bravo");

    showChatActions("Alpha");
    fireEvent.click(within(sidebar()).getByRole("button", { name: "Delete chat Alpha" }));
    expect(await screen.findByText("Start chat")).toBeTruthy();
    expect(screen.queryByLabelText("Chat message")).toBeNull();
    expect(within(sidebar()).queryByText("Alpha")).toBeNull();
    expect(chatService.deleteOne).toHaveBeenCalledWith("a");
    expect(setCurrentChatSessionId).toHaveBeenLastCalledWith(null);

    fireEvent.click(within(sidebar()).getByRole("button", { name: "Select" }));
    fireEvent.click(within(sidebar()).getByRole("checkbox", { name: "Select all" }));
    fireEvent.click(within(sidebar()).getByRole("button", { name: "Delete (2)" }));
    fireEvent.click(
      within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Delete" }),
    );
    await waitFor(() => expect(within(sidebar()).queryAllByRole("listitem")).toHaveLength(0));
    expect(chatService.deleteMany).toHaveBeenCalledTimes(1);
    expect(chatService.deleteMany.mock.calls[0][0].toSorted()).toEqual(["b", "c"]);
  });
});

describe("chat favourites", () => {
  test("starring and unstarring reorder at once, a failure leaves the star alone, and a read from before cannot undo it", async () => {
    widePanel();
    server = [
      summary("a", "Alpha"),
      summary("b", "Beta"),
      summary("c", "Gamma", { isFavorite: true }),
    ];
    render(bubble(), { wrapper: WorkItemHubProvider });
    await openPanel();
    const names = ["Alpha", "Beta", "Gamma"];
    expect(rowOrder(names)).toEqual(["Gamma", "Alpha", "Beta"]);
    expect(star("Gamma").getAttribute("aria-pressed")).toBe("true");
    expect(star("Alpha").getAttribute("aria-pressed")).toBe("false");

    // A history read goes out before the star, and answers after it.
    holdHistoryReads(1);
    emitUnreadChanged("a");
    await waitFor(() => expect(heldReads).toHaveLength(1));

    const starring = deferred<void>();
    chatService.setFavorite.mockImplementationOnce((id: string, favorite: boolean) =>
      starring.promise.then(() => {
        server = server.map((c) => (c.id === id ? { ...c, isFavorite: favorite } : c));
      }),
    );
    fireEvent.click(star("Beta"));
    expect(chatService.setFavorite).toHaveBeenCalledWith("b", true);
    expect((star("Beta") as HTMLButtonElement).disabled).toBe(true);
    expect(star("Beta").getAttribute("aria-pressed")).toBe("false");
    expect((star("Alpha") as HTMLButtonElement).disabled).toBe(false);

    // Shown on success without waiting for any re-read.
    holdHistoryReads(1);
    await act(async () => starring.resolve());
    await flush();
    expect(star("Beta").getAttribute("aria-pressed")).toBe("true");
    expect((star("Beta") as HTMLButtonElement).disabled).toBe(false);
    expect(rowOrder(names)).toEqual(["Beta", "Gamma", "Alpha"]);

    await act(async () =>
      heldReads[0].resolve([
        summary("a", "Alpha"),
        summary("b", "Beta"),
        summary("c", "Gamma", { isFavorite: true }),
      ]),
    );
    await settle();
    expect(star("Beta").getAttribute("aria-pressed")).toBe("true");
    expect(rowOrder(names)).toEqual(["Beta", "Gamma", "Alpha"]);
    for (const held of heldReads.slice(1)) {
      await act(async () => held.resolve(server.map((c) => ({ ...c }))));
    }
    await settle();

    chatService.setFavorite.mockRejectedValueOnce(new Error("favorite failed"));
    fireEvent.click(star("Alpha"));
    await settle();
    expect(chatService.setFavorite).toHaveBeenLastCalledWith("a", true);
    expect(star("Alpha").getAttribute("aria-pressed")).toBe("false");
    expect((star("Alpha") as HTMLButtonElement).disabled).toBe(false);
    expect(rowOrder(names)).toEqual(["Beta", "Gamma", "Alpha"]);

    // Unstarred, Gamma goes back to its last-activity place among the rest.
    fireEvent.click(star("Gamma"));
    await settle();
    expect(chatService.setFavorite).toHaveBeenLastCalledWith("c", false);
    expect(star("Gamma").getAttribute("aria-pressed")).toBe("false");
    expect(rowOrder(names)).toEqual(["Beta", "Alpha", "Gamma"]);
  });
});

describe("chat search", () => {
  test("filters by shown title at once and by message content via a debounced server search, applying only the current query's answer", async () => {
    widePanel();
    server = [
      summary("a", "Deploy pipeline"),
      summary("b", "Bug triage", { isFavorite: true }),
      summary("c", null),
      summary("d", "Release notes", { isFavorite: true }),
    ];
    render(bubble(), { wrapper: WorkItemHubProvider });
    await openPanel();
    const names = ["Deploy pipeline", "Bug triage", "Untitled chat", "Release notes"];
    const box = within(sidebar()).getByLabelText("Search chats");
    const type = (value: string) => fireEvent.change(box, { target: { value } });

    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval"] });
    const advance = (ms: number) =>
      act(() => {
        vi.advanceTimersByTime(ms);
      });

    // Title matches, case-insensitively, before any request; typing on restarts the wait.
    type("DEP");
    expect(rowOrder(names)).toEqual(["Deploy pipeline"]);
    advance(100);
    type("DEPL");
    advance(100);
    expect(chatService.searchChats).not.toHaveBeenCalled();
    advance(1000);
    expect(chatService.searchChats.mock.calls).toEqual([["DEPL"]]);

    // Content matches join in, starred first; an id the list does not hold is ignored.
    await act(async () => searches[0].answer.resolve(["b", "zzz"]));
    await flush();
    expect(rowOrder(names)).toEqual(["Bug triage", "Deploy pipeline"]);

    // A new query drops the old content matches at once.
    type("notes");
    expect(rowOrder(names)).toEqual(["Release notes"]);
    advance(1000);
    type("triage");
    advance(1000);
    expect(searches.map((s) => s.q)).toEqual(["DEPL", "notes", "triage"]);
    await act(async () => searches[2].answer.resolve(["c"]));
    await flush();
    expect(rowOrder(names)).toEqual(["Bug triage", "Untitled chat"]);
    // The answer for "notes" comes back late and is never applied.
    await act(async () => searches[1].answer.resolve(["a"]));
    await flush();
    expect(rowOrder(names)).toEqual(["Bug triage", "Untitled chat"]);

    // Nothing matches; an answer for a query since changed stays unapplied.
    type("qqq");
    expect(within(sidebar()).getByText("Searching…")).toBeTruthy();
    advance(1000);
    type("qqqq");
    await act(async () => searches[3].answer.resolve(["a"]));
    await flush();
    expect(rowOrder(names)).toEqual([]);
    expect(within(sidebar()).getByText("Searching…")).toBeTruthy();
    advance(1000);
    expect(searches[4].q).toBe("qqqq");
    await act(async () => searches[4].answer.resolve([]));
    await flush();
    expect(within(sidebar()).getByText("No chats match")).toBeTruthy();

    // A failed search leaves the title matches; the shown fallback title counts.
    type("untitled");
    expect(rowOrder(names)).toEqual(["Untitled chat"]);
    advance(1000);
    const failed = searches[searches.length - 1];
    expect(failed.q).toBe("untitled");
    await act(async () => failed.answer.reject(new Error("search failed")));
    await flush();
    expect(rowOrder(names)).toEqual(["Untitled chat"]);
    expect(within(sidebar()).queryByText("No chats match")).toBeNull();

    // Blank asks nothing and shows everything.
    const asked = chatService.searchChats.mock.calls.length;
    type("   ");
    advance(1000);
    expect(rowOrder(names)).toEqual([
      "Bug triage",
      "Release notes",
      "Deploy pipeline",
      "Untitled chat",
    ]);
    type("");
    advance(1000);
    expect(chatService.searchChats).toHaveBeenCalledTimes(asked);
    expect(rowOrder(names)).toEqual([
      "Bug triage",
      "Release notes",
      "Deploy pipeline",
      "Untitled chat",
    ]);
    expect(within(sidebar()).queryByText("No chats match")).toBeNull();
  });
});

describe("chat sidebar unread", () => {
  test("unread rows carry the dot; a needs-you chat carries its own different marker", async () => {
    widePanel();
    server = [
      summary("a", "Alpha", { hasUnread: true }),
      summary("b", "Beta", { needsYou: true }),
      summary("c", "Gamma"),
    ];
    render(bubble(), { wrapper: WorkItemHubProvider });
    await openPanel();
    expect(within(row("Alpha")).queryByRole("img", { name: "New messages" })).not.toBeNull();
    expect(within(row("Alpha")).queryByRole("img", { name: "Needs you" })).toBeNull();
    expect(within(row("Beta")).queryByRole("img", { name: "Needs you" })).not.toBeNull();
    expect(within(row("Beta")).queryByRole("img", { name: "New messages" })).toBeNull();
    expect(within(row("Gamma")).queryAllByRole("img")).toHaveLength(0);
  });

  test("narrow: a chat hidden behind the open list is not marked read until the list closes or the panel widens", async () => {
    server = [summary("a", "Alpha")];
    sessions.a = chat("a", "Alpha", [msg("a", 0, "user"), msg("a", 1, "assistant")]);
    render(bubble(), { wrapper: WorkItemHubProvider });
    await openPanel();
    fireEvent.click(toggle());
    await pick("Alpha");
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("a", 1));
    await settle();

    fireEvent.click(toggle());
    emitAppended("a", msg("a", 2, "assistant"));
    await settle();
    expect(chatService.markRead).not.toHaveBeenCalledWith("a", 2);
    fireEvent.click(toggle());
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("a", 2));
    await settle();

    fireEvent.click(toggle());
    emitAppended("a", msg("a", 3, "assistant"));
    await settle();
    expect(chatService.markRead).not.toHaveBeenCalledWith("a", 3);
    dragResizeHandle(300);
    expect(querySidebar()).not.toBeNull();
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("a", 3));
  });

  test("wide: a reply in another chat lights its row live, and a reply in the chat beside the list is read", async () => {
    widePanel();
    server = [summary("a", "Alpha"), summary("b", "Beta")];
    sessions.a = chat("a", "Alpha", [msg("a", 0, "user")]);
    render(bubble(), { wrapper: WorkItemHubProvider });
    await openPanel();
    await pick("Alpha");
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("a", 0));

    setUnread("b", true);
    emitUnreadChanged("b");
    await waitFor(() =>
      expect(within(row("Beta")).queryByRole("img", { name: "New messages" })).not.toBeNull(),
    );
    expect(within(row("Alpha")).queryByRole("img", { name: "New messages" })).toBeNull();

    emitAppended("a", msg("a", 1, "assistant"));
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("a", 1));
  });

  test("the toggle shows how many chats are unread only while the list is hidden", async () => {
    server = [summary("a", "Alpha"), summary("b", "Beta")];
    render(bubble(), { wrapper: WorkItemHubProvider });
    await openPanel();
    expect(within(header()).queryByText(/^\d+$/)).toBeNull();

    setUnread("b", true);
    emitUnreadChanged("b");
    await waitFor(() => expect(badge(1)).not.toBeNull());
    setUnread("a", true);
    emitUnreadChanged("a");
    await waitFor(() => expect(badge(2)).not.toBeNull());
    expect(badge(1)).toBeNull();

    fireEvent.click(toggle());
    expect(within(header()).queryByText(/^\d+$/)).toBeNull();
    expect(within(row("Alpha")).queryByRole("img", { name: "New messages" })).not.toBeNull();
    expect(within(row("Beta")).queryByRole("img", { name: "New messages" })).not.toBeNull();

    fireEvent.click(toggle());
    expect(badge(2)).not.toBeNull();
  });
});

describe("bulk delete mode", () => {
  const boxOf = (name: string) =>
    within(sidebar()).getByRole("checkbox", { name: `Select chat ${name}` }) as HTMLInputElement;
  const selectAll = () =>
    within(sidebar()).getByRole("checkbox", { name: "Select all" }) as HTMLInputElement;
  const enterSelecting = () =>
    fireEvent.click(within(sidebar()).getByRole("button", { name: "Select" }));
  const deleteButton = (count: number) =>
    within(sidebar()).getByRole("button", { name: `Delete (${count})` }) as HTMLButtonElement;

  async function openWithThree() {
    widePanel();
    server = [summary("a", "Alpha"), summary("b", "Beta"), summary("c", null)];
    sessions.a = chat("a", "Alpha", [msg("a", 0, "user")]);
    sessions.b = chat("b", "Beta", [msg("b", 0, "user")]);
    render(bubble(), { wrapper: WorkItemHubProvider });
    await openPanel();
  }

  test("the list has no Delete all, and outside selection mode has no checkboxes and a row still opens its chat", async () => {
    await openWithThree();

    expect(within(sidebar()).queryByRole("button", { name: "Delete all" })).toBeNull();
    expect(within(sidebar()).queryAllByRole("checkbox")).toHaveLength(0);
    await pick("Alpha");
    expect(chatService.getById).toHaveBeenCalledWith("a");
  });

  test("Select shows a checkbox at the end of every row, hides the row menus, and toggling never opens a chat", async () => {
    await openWithThree();
    showChatActions("Beta");
    enterSelecting();

    expect(within(sidebar()).queryAllByRole("checkbox", { name: /^Select chat / })).toHaveLength(3);
    for (const name of ["Alpha", "Beta", "Untitled chat"]) {
      expect(boxOf(name).checked).toBe(false);
      const li = row(name);
      expect(li.contains(boxOf(name))).toBe(true);
      expect(li.querySelector("input[type=checkbox]")).toBe(boxOf(name));
    }
    expect(within(sidebar()).queryByRole("button", { name: /^Actions for chat/ })).toBeNull();
    expect(within(sidebar()).queryByRole("button", { name: /^Favorite chat/ })).toBeNull();
    expect(within(sidebar()).queryByRole("button", { name: /^Delete chat/ })).toBeNull();
    expect(within(sidebar()).queryByRole("button", { name: "Select" })).toBeNull();
    expect(deleteButton(0).disabled).toBe(true);
    expect(within(sidebar()).getByRole("button", { name: "Cancel" })).toBeTruthy();

    fireEvent.click(within(sidebar()).getByText("Alpha"));
    expect(boxOf("Alpha").checked).toBe(true);
    fireEvent.click(boxOf("Alpha"));
    expect(boxOf("Alpha").checked).toBe(false);
    fireEvent.click(boxOf("Beta"));
    await settle();

    expect(boxOf("Beta").checked).toBe(true);
    expect(chatService.getById).not.toHaveBeenCalled();
    expect(screen.queryByLabelText("Chat message")).toBeNull();
    expect(deleteButton(1).disabled).toBe(false);
  });

  test("a row click in selection mode leaves the open chat open", async () => {
    await openWithThree();
    await pick("Alpha");
    chatService.getById.mockClear();
    enterSelecting();

    fireEvent.click(within(sidebar()).getByText("Beta"));
    await settle();

    expect(boxOf("Beta").checked).toBe(true);
    expect(chatService.getById).not.toHaveBeenCalled();
    expect(screen.getByLabelText("Chat message")).toBeTruthy();
  });

  test("Select all checks every row, goes indeterminate when one is unchecked, and follows manual checks", async () => {
    await openWithThree();
    enterSelecting();

    fireEvent.click(selectAll());
    for (const n of ["Alpha", "Beta", "Untitled chat"]) expect(boxOf(n).checked).toBe(true);
    expect(selectAll().checked).toBe(true);
    expect(selectAll().indeterminate).toBe(false);
    expect(deleteButton(3)).toBeTruthy();

    fireEvent.click(boxOf("Beta"));
    expect(selectAll().checked).toBe(false);
    expect(selectAll().indeterminate).toBe(true);
    expect(deleteButton(2)).toBeTruthy();

    fireEvent.click(boxOf("Beta"));
    expect(selectAll().checked).toBe(true);
    expect(selectAll().indeterminate).toBe(false);

    fireEvent.click(selectAll());
    for (const n of ["Alpha", "Beta", "Untitled chat"]) expect(boxOf(n).checked).toBe(false);
    expect(selectAll().indeterminate).toBe(false);
    expect(deleteButton(0).disabled).toBe(true);

    fireEvent.click(boxOf("Alpha"));
    fireEvent.click(selectAll());
    for (const n of ["Alpha", "Beta", "Untitled chat"]) expect(boxOf(n).checked).toBe(true);
  });

  test("Select all and the count only cover the rows the search leaves listed", async () => {
    await openWithThree();
    enterSelecting();

    fireEvent.change(within(sidebar()).getByLabelText("Search chats"), {
      target: { value: "alp" },
    });
    fireEvent.click(selectAll());
    expect(deleteButton(1)).toBeTruthy();
    fireEvent.click(deleteButton(1));
    fireEvent.click(
      within(await screen.findByRole("alertdialog", { name: "Delete 1 chat?" })).getByRole(
        "button",
        {
          name: "Delete",
        },
      ),
    );

    await waitFor(() => expect(chatService.deleteMany).toHaveBeenCalledTimes(1));
    expect(chatService.deleteMany).toHaveBeenCalledWith(["a"]);
  });

  test("Delete asks first, its Cancel keeps the checks, and nothing is requested until it is confirmed", async () => {
    await openWithThree();
    enterSelecting();
    fireEvent.click(boxOf("Alpha"));
    fireEvent.click(boxOf("Beta"));

    fireEvent.click(deleteButton(2));
    const dialog = await screen.findByRole("alertdialog", { name: "Delete 2 chats?" });
    expect(within(dialog).getByText("Delete 2 chats? This can't be undone.")).toBeTruthy();
    expect(chatService.deleteMany).not.toHaveBeenCalled();
    expect(chatService.deleteOne).not.toHaveBeenCalled();

    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("alertdialog")).toBeNull();
    expect(boxOf("Alpha").checked).toBe(true);
    expect(boxOf("Beta").checked).toBe(true);
    expect(boxOf("Untitled chat").checked).toBe(false);
    expect(chatService.deleteMany).not.toHaveBeenCalled();
  });

  test("the selection-mode Cancel leaves without a request and the next Select starts unchecked", async () => {
    await openWithThree();
    enterSelecting();
    fireEvent.click(selectAll());

    fireEvent.click(within(sidebar()).getByRole("button", { name: "Cancel" }));
    expect(within(sidebar()).queryAllByRole("checkbox")).toHaveLength(0);
    expect(within(sidebar()).getByRole("button", { name: "Select" })).toBeTruthy();

    enterSelecting();
    for (const n of ["Alpha", "Beta", "Untitled chat"]) expect(boxOf(n).checked).toBe(false);
    expect(selectAll().checked).toBe(false);
    expect(chatService.deleteMany).not.toHaveBeenCalled();
    expect(chatService.deleteOne).not.toHaveBeenCalled();
  });

  test("confirming sends one batch of only the checked ids, ends selection mode and keeps the other chats", async () => {
    await openWithThree();
    enterSelecting();
    fireEvent.click(boxOf("Alpha"));
    fireEvent.click(boxOf("Untitled chat"));
    fireEvent.click(deleteButton(2));
    fireEvent.click(
      within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Delete" }),
    );

    await waitFor(() => expect(within(sidebar()).queryByText("Alpha")).toBeNull());
    expect(chatService.deleteMany).toHaveBeenCalledTimes(1);
    expect(chatService.deleteMany.mock.calls[0][0].toSorted()).toEqual(["a", "c"]);
    expect(chatService.deleteOne).not.toHaveBeenCalled();
    expect(within(sidebar()).getByText("Beta")).toBeTruthy();
    expect(within(sidebar()).queryByText("Untitled chat")).toBeNull();
    expect(within(sidebar()).queryAllByRole("checkbox")).toHaveLength(0);
    expect(within(sidebar()).getByRole("button", { name: "Select" })).toBeTruthy();
  });

  test("deleting the open chat in a batch leaves it for the start form; deleting others does not", async () => {
    await openWithThree();
    await pick("Alpha");
    enterSelecting();
    fireEvent.click(boxOf("Beta"));
    fireEvent.click(deleteButton(1));
    fireEvent.click(
      within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Delete" }),
    );
    await waitFor(() => expect(within(sidebar()).queryByText("Beta")).toBeNull());
    expect(screen.getByLabelText("Chat message")).toBeTruthy();
    expect(setCurrentChatSessionId).not.toHaveBeenLastCalledWith(null);

    enterSelecting();
    fireEvent.click(boxOf("Alpha"));
    fireEvent.click(boxOf("Untitled chat"));
    fireEvent.click(deleteButton(2));
    fireEvent.click(
      within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Delete" }),
    );

    expect(await screen.findByText("Start chat")).toBeTruthy();
    expect(screen.queryByLabelText("Chat message")).toBeNull();
    expect(setCurrentChatSessionId).toHaveBeenLastCalledWith(null);
    expect(chatService.deleteMany).toHaveBeenCalledTimes(2);
  });

  test("a failed batch is logged and the list is re-read, so chats the server kept come back", async () => {
    await openWithThree();
    const logged = vi.spyOn(console, "error").mockImplementation(() => {});
    chatService.deleteMany.mockImplementationOnce((ids: string[]) => {
      server = server.filter((c) => c.id !== ids[0]);
      return Promise.reject(new Error("boom"));
    });
    enterSelecting();
    fireEvent.click(selectAll());
    fireEvent.click(deleteButton(3));
    fireEvent.click(
      within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Delete" }),
    );

    await waitFor(() => expect(logged).toHaveBeenCalled());
    await waitFor(() => expect(within(sidebar()).queryAllByRole("listitem")).toHaveLength(2));
    expect(chatService.deleteMany).toHaveBeenCalledTimes(1);
    logged.mockRestore();
  });

  test("selection mode ends when the last chat goes", async () => {
    await openWithThree();
    enterSelecting();
    fireEvent.click(boxOf("Alpha"));

    server = [];
    emitUnreadChanged("a");
    await waitFor(() => expect(within(sidebar()).queryAllByRole("listitem")).toHaveLength(0));
    expect(within(sidebar()).queryByRole("checkbox", { name: "Select all" })).toBeNull();

    server = [summary("z", "Zulu")];
    emitUnreadChanged("a");
    await within(sidebar()).findByText("Zulu");
    expect(within(sidebar()).queryAllByRole("checkbox")).toHaveLength(0);
    expect(within(sidebar()).getByRole("button", { name: "Select" })).toBeTruthy();
  });
});
