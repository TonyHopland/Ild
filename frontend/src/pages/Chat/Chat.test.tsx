import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor, act, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation, useNavigate } from "react-router";
import type { AiProvider, ChatMessage, ChatSession, ChatSessionSummary, User } from "../../types";
import {
  openChatFromList,
  openChatList,
  showChatActions,
  type ChatHubEvents,
} from "../../test-support";

const {
  handlers,
  invoke,
  chatService,
  aiProviderService,
  workItemService,
  authService,
  getOpenLoopDocument,
} = vi.hoisted(() => ({
  // Every handler registered for an event, across every mounted hub connection:
  // the inbox, the bubble and the page each hold their own, as the real hook does.
  handlers: {} as Record<string, Set<(msg: { payload: unknown }) => void>>,
  invoke: vi.fn((..._args: unknown[]) => Promise.resolve()),
  chatService: {
    listHistory: vi.fn(),
    getById: vi.fn(),
    start: vi.fn(),
    sendMessage: vi.fn(),
    interrupt: vi.fn(),
    deleteOne: vi.fn(),
    deleteAll: vi.fn(),
    searchChats: vi.fn(() => Promise.resolve([] as string[])),
    markRead: vi.fn(),
    rename: vi.fn(() => Promise.resolve()),
    setFavorite: vi.fn(() => Promise.resolve()),
  },
  aiProviderService: { getAll: vi.fn() },
  workItemService: { listEditProposalsFor: vi.fn(() => Promise.resolve([])) },
  authService: {
    getUser: vi.fn(),
    getToken: vi.fn(() => "test-token"),
    getMe: vi.fn(),
    clearAuth: vi.fn(),
    logout: vi.fn(() => Promise.resolve()),
    onTokenChange: vi.fn(() => () => {}),
  },
  getOpenLoopDocument: vi.fn(),
}));

vi.mock("../../hooks/useSignalR", () => {
  const on = (event: string, handler: (msg: { payload: unknown }) => void) => {
    (handlers[event] ??= new Set()).add(handler);
  };
  const off = (event: string, handler: (msg: { payload: unknown }) => void) => {
    handlers[event]?.delete(handler);
  };
  return { useSignalR: () => ({ connectionState: "connected", on, off, invoke }) };
});

vi.mock("../../services/auth", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../services/auth")>()),
  chatService,
  aiProviderService,
  workItemService,
  authService,
}));
vi.mock("../../utils/openLoopDocument", () => ({ getOpenLoopDocument }));

import App from "../../App";
import ChatBubble from "../../components/ChatBubble";
import { ChatInboxProvider } from "../../components/ChatInbox";
import ChatSidebar from "../../components/ChatSidebar";
import Header from "../../components/Header";
import { AuthContext } from "../../hooks/useAuth";
import { setChatEnabled } from "../../hooks/useChatEnabled";
import ChatPage from ".";

// The page does not explain that it sends no context: a reviewer asked for the line to go.
const NO_CONTEXT = "No work item or loop open: the agent only sees this conversation";

const user: User = { id: "1", username: "test", createdAt: "" };

const provider: AiProvider = {
  id: "p1",
  name: "Claude",
  type: "claude-code",
  baseUrl: "",
  apiKey: "",
  model: "",
  isDefault: true,
  parallelism: 1,
  createdAt: "2026-01-01T00:00:00Z",
};

// What the server holds: the history GET /chat/history answers with (in the
// server's own order, newest activity first), and each chat GET /chat/{id} answers
// with. A test changes them to say what the server has come to know.
let server: ChatSessionSummary[] = [];
const sessions: Record<string, ChatSession> = {};

function summary(
  id: string,
  name: string,
  updatedAt: string,
  extra: { hasUnread?: boolean; isBusy?: boolean } = {},
): ChatSessionSummary {
  return {
    id,
    name,
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt,
    hasUnread: extra.hasUnread ?? false,
    ...(extra.isBusy !== undefined ? { isBusy: extra.isBusy } : {}),
  } as ChatSessionSummary;
}

function msg(
  id: string,
  sequence: number,
  role: "user" | "assistant",
  content: string,
): ChatMessage {
  return { id, role, content, interrupted: false, sequence, createdAt: "2026-01-01T00:00:00Z" };
}

function session(
  id: string,
  name: string,
  messages: ChatMessage[],
  activeTurnId: string | null = null,
): ChatSession {
  return {
    id,
    name,
    aiProviderId: "p1",
    providerType: "claude-code",
    tools: ["ild"],
    createdAt: "2026-01-01T00:00:00Z",
    messages,
    activeTurnId,
  };
}

/** Two chats, "First" with the newest activity and "Second" older, both idle. */
function seedTwoChats() {
  server = [
    summary("s1", "First", "2026-01-03T00:00:00Z"),
    summary("s2", "Second", "2026-01-02T00:00:00Z"),
  ];
  sessions.s1 = session("s1", "First", [
    msg("s1-0", 0, "user", "s1 question"),
    msg("s1-1", 1, "assistant", "s1 reply"),
  ]);
  sessions.s2 = session("s2", "Second", [
    msg("s2-0", 0, "user", "s2 question"),
    msg("s2-1", 1, "assistant", "s2 reply"),
  ]);
}

function patchServer(id: string, change: Partial<ChatSessionSummary> & { isBusy?: boolean }) {
  server = server.map((c) => (c.id === id ? ({ ...c, ...change } as ChatSessionSummary) : c));
}

beforeEach(() => {
  chatService.listHistory.mockImplementation(() => Promise.resolve(server.map((c) => ({ ...c }))));
  chatService.getById.mockImplementation((id: string) =>
    sessions[id]
      ? Promise.resolve(structuredClone(sessions[id]))
      : Promise.reject({ message: "Chat not found." }),
  );
  chatService.markRead.mockImplementation((id: string) => {
    patchServer(id, { hasUnread: false });
    return Promise.resolve();
  });
  chatService.deleteOne.mockImplementation((id: string) => {
    server = server.filter((c) => c.id !== id);
    delete sessions[id];
    return Promise.resolve();
  });
  chatService.deleteAll.mockImplementation(() => {
    server = [];
    return Promise.resolve();
  });
  chatService.sendMessage.mockResolvedValue("t-sent");
  chatService.interrupt.mockResolvedValue(undefined);
  aiProviderService.getAll.mockResolvedValue([provider]);
  authService.getUser.mockReturnValue(user);
  authService.getMe.mockResolvedValue(user);
  getOpenLoopDocument.mockReturnValue(null);
  // The footer asks for the version; nothing here may reach the network.
  vi.stubGlobal(
    "fetch",
    vi.fn(() => Promise.reject(new Error("offline"))),
  );
});

afterEach(() => {
  cleanup();
  for (const k of Object.keys(handlers)) delete handlers[k];
  for (const k of Object.keys(sessions)) delete sessions[k];
  server = [];
  vi.clearAllMocks();
  vi.unstubAllGlobals();
  localStorage.clear();
  window.history.replaceState(null, "", "/");
});

function LocationProbe() {
  const location = useLocation();
  const navigate = useNavigate();
  return (
    <>
      <span data-testid="location">{location.pathname}</span>
      {/* The browser's Back button. */}
      <button type="button" onClick={() => void navigate(-1)}>
        History back
      </button>
    </>
  );
}

function currentPath() {
  return screen.getByTestId("location").textContent;
}

/**
 * The authenticated shell as App.tsx composes it: one inbox around the header, the
 * routed page and the globally mounted bubble. Any route other than the Chat tab
 * stands in for the rest of the app.
 */
function renderShell(initialPath: string) {
  const auth = {
    user,
    token: "test-token",
    isAuthenticated: true,
    isLoading: false,
    login: vi.fn(),
    logout: vi.fn(),
  };
  return render(
    <MemoryRouter initialEntries={[initialPath]}>
      <AuthContext.Provider value={auth}>
        <ChatInboxProvider>
          <Header />
          <Routes>
            <Route path="/chat" element={<ChatPage />} />
            <Route path="/chat/:chatId" element={<ChatPage />} />
            <Route path="*" element={<p>Another page</p>} />
          </Routes>
          <ChatBubble />
          <LocationProbe />
        </ChatInboxProvider>
      </AuthContext.Provider>
    </MemoryRouter>,
  );
}

/** Deliver one hub event to every handler registered for it. */
function emit<E extends keyof ChatHubEvents>(event: E, payload: ChatHubEvents[E]): void;
function emit(
  event: "ChatUnreadChanged" | "ChatTitleChanged" | "ChatActivityChanged",
  payload: { chatSessionId: string },
): void;
function emit(event: string, payload: unknown) {
  act(() => {
    for (const handler of Array.from(handlers[event] ?? [])) handler({ payload });
  });
}

/** Let every answer already given reach the view, and every effect it causes run. */
async function settle() {
  for (let i = 0; i < 3; i++) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
  }
}

function chatLink() {
  return within(screen.getByRole("navigation")).getByRole("link", { name: "Chat" });
}

/** The text of whatever describes an element to a screen reader. */
function description(el: HTMLElement): string {
  return (el.getAttribute("aria-describedby") ?? "")
    .split(/\s+/)
    .filter(Boolean)
    .map((id) => document.getElementById(id)?.textContent ?? "")
    .join(" ")
    .trim();
}

function pageSidebar() {
  return screen.getByRole("complementary", { name: "Chats" });
}

function rowOf(sidebar: HTMLElement, name: string): HTMLElement {
  return within(sidebar).getByText(name).closest("li") as HTMLElement;
}

function busyMarker(row: HTMLElement) {
  return within(row).queryByLabelText(/working|busy/i);
}

/** How many joins of a hub group are still held: joins minus leaves. */
function heldJoins(method: "SubscribeToChat" | "SubscribeToChatInbox", id?: string): number {
  const leave = method === "SubscribeToChat" ? "UnsubscribeFromChat" : "UnsubscribeFromChatInbox";
  const matches = (args: unknown[], name: string) =>
    args[0] === name && (id === undefined || args[1] === id);
  const calls = invoke.mock.calls as unknown[][];
  return (
    calls.filter((a) => matches(a, method)).length - calls.filter((a) => matches(a, leave)).length
  );
}

/** The open chat's title as the page's header shows it, outside the chat list. */
function pageTitleShows(name: string): boolean {
  const sidebar = screen.queryByRole("complementary", { name: "Chats" });
  return screen.queryAllByText(name).some((el) => !sidebar?.contains(el));
}

describe("Chat tab in the main navigation", () => {
  test("carries the count of unread chats as its description, live, and nothing at zero", async () => {
    server = [
      summary("s1", "First", "2026-01-03T00:00:00Z", { hasUnread: true }),
      summary("s2", "Second", "2026-01-02T00:00:00Z", { hasUnread: true }),
      summary("s3", "Third", "2026-01-01T00:00:00Z"),
    ];
    renderShell("/taskboard");

    await waitFor(() => expect(description(chatLink())).toMatch(/\b2\b/));
    // The badge is not part of the link's name.
    expect(chatLink()).toBeTruthy();

    patchServer("s1", { hasUnread: false });
    emit("ChatUnreadChanged", { chatSessionId: "s1" });
    await waitFor(() => expect(description(chatLink())).toMatch(/\b1\b/));

    patchServer("s2", { hasUnread: false });
    emit("ChatUnreadChanged", { chatSessionId: "s2" });
    await waitFor(() => expect(description(chatLink())).toBe(""));
    expect(chatLink().textContent).toBe("Chat");
  });

  test("is there and opens the page with the chat bubble switched off", async () => {
    seedTwoChats();
    setChatEnabled(false);
    renderShell("/taskboard");
    await settle();
    expect(screen.queryByRole("button", { name: "Open chat" })).toBeNull();

    fireEvent.click(chatLink());

    expect(await screen.findByText("s1 reply")).toBeTruthy();
    await waitFor(() => expect(currentPath()).toBe("/chat/s1"));
  });

  test.each([
    ["/chat/s1", "s1 reply"],
    ["/chat", "Start chat"],
  ])(
    "App renders %s as the Chat page inside the signed-in shell, without the bubble",
    async (path, shown) => {
      if (path === "/chat/s1") seedTwoChats();
      window.history.replaceState(null, "", path);
      render(<App />);

      expect(await screen.findByText(shown)).toBeTruthy();
      expect(screen.queryByText(NO_CONTEXT)).toBeNull();
      expect(
        within(screen.getByRole("navigation")).getByRole("link", { name: "Chat" }),
      ).toBeTruthy();
      expect(window.location.pathname).toBe(path);
      expect(screen.queryByRole("button", { name: "Open chat" })).toBeNull();
      expect(screen.queryByRole("dialog", { name: "AI chat" })).toBeNull();
    },
  );
});

describe("Opening the Chat tab", () => {
  test("opens the chat the bubble has open, hides the bubble, and hands the page's chat back to it", async () => {
    seedTwoChats();
    renderShell("/taskboard");

    fireEvent.click(await screen.findByRole("button", { name: "Open chat" }));
    await openChatFromList("Second");
    const bubble = screen.getByRole("dialog", { name: "AI chat" });
    expect(await within(bubble).findByText("s2 reply")).toBeTruthy();
    const listExpanded = within(bubble)
      .getByRole("button", { name: "Chat list" })
      .getAttribute("aria-expanded");
    // There is no way from the bubble to the page other than the tab.
    expect(within(bubble).queryAllByRole("link")).toEqual([]);
    expect(
      within(bubble).queryByRole("button", { name: /full ?screen|expand|open in/i }),
    ).toBeNull();

    fireEvent.click(chatLink());

    await waitFor(() => expect(currentPath()).toBe("/chat/s2"));
    expect(await screen.findByText("s2 reply")).toBeTruthy();
    expect(pageTitleShows("Second")).toBe(true);
    expect(screen.queryByRole("dialog", { name: "AI chat" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Open chat" })).toBeNull();
    // One inbox for the whole app, however many places show its list.
    expect(heldJoins("SubscribeToChatInbox")).toBe(1);

    fireEvent.click(within(pageSidebar()).getByText("First"));
    await waitFor(() => expect(currentPath()).toBe("/chat/s1"));
    expect(await screen.findByText("s1 reply")).toBeTruthy();
    await settle();
    expect(screen.queryByText("s2 reply")).toBeNull();
    // The bubble's conversation is not mounted on the tab, so nothing is left
    // listening to the chat it had open before.
    expect(heldJoins("SubscribeToChat", "s2")).toBe(0);

    fireEvent.click(
      within(screen.getByRole("navigation")).getByRole("link", { name: "Taskboard" }),
    );

    // The bubble comes back as it was left, now on the chat the page had open.
    const back = await screen.findByRole("dialog", { name: "AI chat" });
    expect(
      within(back).getByRole("button", { name: "Chat list" }).getAttribute("aria-expanded"),
    ).toBe(listExpanded);
    expect(await within(back).findByText("s1 reply")).toBeTruthy();
    expect(within(back).queryByText("s2 reply")).toBeNull();
  });

  test("opens the chat with the newest activity when the bubble has none open", async () => {
    seedTwoChats();
    renderShell("/chat");

    await waitFor(() => expect(currentPath()).toBe("/chat/s1"));
    expect(await screen.findByText("s1 reply")).toBeTruthy();
    expect(screen.queryByText(NO_CONTEXT)).toBeNull();
    expect(screen.queryByRole("button", { name: "Open chat" })).toBeNull();
    // Marked read up to its newest message, once.
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("s1", 1));
    await settle();
    expect(chatService.markRead).toHaveBeenCalledTimes(1);
  });

  test("with no chats shows the start form, and starting or leaving a chat moves the URL", async () => {
    sessions.s9 = session("s9", "Fresh", [msg("s9-0", 0, "assistant", "Hello there")]);
    chatService.start.mockImplementation(() => {
      server = [summary("s9", "Fresh", "2026-01-04T00:00:00Z")];
      return Promise.resolve(structuredClone(sessions.s9));
    });
    renderShell("/chat");

    const start = await screen.findByRole("button", { name: "Start chat" });
    expect(screen.getByLabelText("AI provider")).toBeTruthy();
    expect(screen.queryByText(NO_CONTEXT)).toBeNull();
    await waitFor(() =>
      expect((screen.getByLabelText("AI provider") as HTMLSelectElement).value).toBe("p1"),
    );
    expect(currentPath()).toBe("/chat");

    fireEvent.click(start);

    await waitFor(() => expect(currentPath()).toBe("/chat/s9"));
    expect(await screen.findByText("Hello there")).toBeTruthy();
    expect(screen.getByRole("textbox", { name: "Chat message" })).toBeTruthy();
    expect(screen.queryByText(NO_CONTEXT)).toBeNull();

    fireEvent.click(within(pageSidebar()).getByRole("button", { name: "New chat" }));

    await waitFor(() => expect(currentPath()).toBe("/chat"));
    expect(await screen.findByRole("button", { name: "Start chat" })).toBeTruthy();
    await settle();
    // The start form stays: leaving a chat is not a reason to open another one.
    expect(currentPath()).toBe("/chat");
    expect(screen.queryByText("Hello there")).toBeNull();
  });
});

describe("Opening a chat by URL", () => {
  test("/chat/<id> opens that chat, not the newest one", async () => {
    seedTwoChats();
    renderShell("/chat/s2");

    expect(await screen.findByText("s2 reply")).toBeTruthy();
    expect(pageTitleShows("Second")).toBe(true);
    expect(screen.getByRole("textbox", { name: "Chat message" })).toBeTruthy();
    expect(screen.queryByText(NO_CONTEXT)).toBeNull();
    await settle();
    expect(currentPath()).toBe("/chat/s2");
    expect(screen.queryByText("s1 reply")).toBeNull();
    expect(chatService.markRead).toHaveBeenCalledWith("s2", 1);
    expect(chatService.markRead).not.toHaveBeenCalledWith("s1", expect.anything());
  });

  test("an id that cannot be opened shows the error and the start form, never another chat", async () => {
    seedTwoChats();
    renderShell("/chat/gone");

    expect(await screen.findByText("Chat not found.")).toBeTruthy();
    expect(await screen.findByRole("button", { name: "Start chat" })).toBeTruthy();
    await settle();
    expect(screen.queryByText("s1 reply")).toBeNull();
    expect(screen.queryByText("s2 reply")).toBeNull();
    expect(currentPath()).not.toMatch(/^\/chat\/s[12]$/);
  });

  test("a chat still opening when the page moves on never becomes the bubble's chat", async () => {
    seedTwoChats();
    let answerFirst: (chat: ChatSession) => void = () => {};
    chatService.getById.mockImplementation((id: string) =>
      id === "s1"
        ? new Promise<ChatSession>((resolve) => {
            answerFirst = resolve;
          })
        : Promise.resolve(structuredClone(sessions[id])),
    );
    renderShell("/chat/s1");

    fireEvent.click(
      await within(await screen.findByRole("complementary", { name: "Chats" })).findByText(
        "Second",
      ),
    );
    await waitFor(() => expect(currentPath()).toBe("/chat/s2"));
    expect(await screen.findByText("s2 reply")).toBeTruthy();

    answerFirst(structuredClone(sessions.s1));
    await settle();
    expect(currentPath()).toBe("/chat/s2");
    expect(screen.queryByText("s1 reply")).toBeNull();

    fireEvent.click(
      within(screen.getByRole("navigation")).getByRole("link", { name: "Taskboard" }),
    );
    fireEvent.click(await screen.findByRole("button", { name: "Open chat" }));
    const bubble = await screen.findByRole("dialog", { name: "AI chat" });
    expect(await within(bubble).findByText("s2 reply")).toBeTruthy();
    expect(within(bubble).queryByText("s1 reply")).toBeNull();
  });
});

describe("Sending from the Chat tab", () => {
  test("carries no work item or loop context", async () => {
    seedTwoChats();
    getOpenLoopDocument.mockReturnValue({ format: "ild-loop-template/v2", nodes: [] });
    renderShell("/chat/s1");

    const box = await screen.findByRole("textbox", { name: "Chat message" });
    fireEvent.change(box, { target: { value: "hello" } });
    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    await waitFor(() => expect(chatService.sendMessage).toHaveBeenCalledTimes(1));
    expect(chatService.sendMessage).toHaveBeenCalledWith("s1", "hello", null, null);
  });
});

describe("The page's chat list and conversation", () => {
  test("search, delete and stop work on the page as in the bubble", async () => {
    seedTwoChats();
    sessions.s1 = { ...sessions.s1, activeTurnId: "t1" };
    renderShell("/chat/s1");

    expect(await screen.findByText("s1 reply")).toBeTruthy();
    await waitFor(() => expect(screen.getByRole("status").textContent).toMatch(/Thinking/));

    fireEvent.change(within(pageSidebar()).getByRole("searchbox", { name: "Search chats" }), {
      target: { value: "Sec" },
    });
    await waitFor(() => expect(within(pageSidebar()).queryByText("First")).toBeNull());
    expect(within(pageSidebar()).getByText("Second")).toBeTruthy();
    fireEvent.change(within(pageSidebar()).getByRole("searchbox", { name: "Search chats" }), {
      target: { value: "" },
    });

    showChatActions("Second");
    fireEvent.click(screen.getByRole("button", { name: "Delete chat Second" }));
    await waitFor(() => expect(within(pageSidebar()).queryByText("Second")).toBeNull());
    expect(chatService.deleteOne).toHaveBeenCalledWith("s2");
    expect(currentPath()).toBe("/chat/s1");
    expect(screen.getByText("s1 reply")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Stop" }));
    await waitFor(() => expect(chatService.interrupt).toHaveBeenCalledWith("s1"));
  });

  test("a sidebar given extra entries shows them in the list", () => {
    render(
      <ChatSidebar
        history={[]}
        currentChatId={null}
        full={false}
        onOpen={() => {}}
        onNewChat={() => {}}
        onDelete={() => {}}
        onDeleteAll={() => {}}
        renameChat={() => Promise.resolve(true)}
        renamesInFlight={new Set()}
        onFavorite={() => {}}
        favoritesInFlight={new Set()}
        extras={<a href="/chat/schedules">Schedules</a>}
      />,
    );

    expect(
      within(screen.getByRole("complementary", { name: "Chats" })).getByRole("link", {
        name: "Schedules",
      }),
    ).toBeTruthy();
  });
});

describe("Several chats busy at once", () => {
  test("a turn finishing in a background chat marks it unread and leaves the open chat alone", async () => {
    seedTwoChats();
    sessions.s2 = { ...sessions.s2, activeTurnId: "tB" };
    renderShell("/chat/s2");

    expect(await screen.findByText("s2 reply")).toBeTruthy();
    emit("ChatTurnProgress", { chatSessionId: "s2", turnId: "tB", delta: "B partial" });
    expect(await screen.findByText("B partial")).toBeTruthy();
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("s2", 1));
    await settle();

    emit("ChatTurnStarted", { chatSessionId: "s1", turnId: "tA" });
    emit("ChatTurnProgress", { chatSessionId: "s1", turnId: "tA", delta: "A background text" });
    emit("ChatMessageAppended", {
      chatSessionId: "s1",
      turnId: "tA",
      message: msg("s1-2", 2, "assistant", "A finished reply"),
    });
    emit("ChatTurnCompleted", { chatSessionId: "s1", turnId: "tA", interrupted: false });
    patchServer("s1", { hasUnread: true });
    emit("ChatUnreadChanged", { chatSessionId: "s1" });

    await waitFor(() =>
      expect(
        within(rowOf(pageSidebar(), "First")).queryByRole("img", { name: "New messages" }),
      ).toBeTruthy(),
    );
    await waitFor(() => expect(description(chatLink())).toMatch(/\b1\b/));
    await settle();

    // The open chat is exactly as it was.
    expect(currentPath()).toBe("/chat/s2");
    expect(screen.getByText("s2 reply")).toBeTruthy();
    expect(screen.getByText("B partial")).toBeTruthy();
    expect(screen.getByRole("status").textContent).toMatch(/Responding/);
    expect(screen.getByRole("button", { name: "Stop" })).toBeTruthy();
    expect(screen.queryByText("A background text")).toBeNull();
    expect(screen.queryByText("A finished reply")).toBeNull();
    expect(
      within(rowOf(pageSidebar(), "Second")).queryByRole("img", { name: "New messages" }),
    ).toBeNull();
    expect(chatService.markRead.mock.calls).toEqual([["s2", 1]]);
  });

  test("the busy marker on other chats follows the server's history, in the bubble and on the page", async () => {
    server = [
      summary("s1", "First", "2026-01-03T00:00:00Z", { isBusy: true }),
      summary("s2", "Second", "2026-01-02T00:00:00Z", { isBusy: false }),
    ];
    sessions.s1 = session("s1", "First", [msg("s1-0", 0, "user", "s1 question")], "tA");
    sessions.s2 = session("s2", "Second", [msg("s2-0", 0, "user", "s2 question")]);
    renderShell("/taskboard");

    fireEvent.click(await screen.findByRole("button", { name: "Open chat" }));
    const bubbleList = await openChatList();
    await waitFor(() => expect(busyMarker(rowOf(bubbleList, "First"))).toBeTruthy());
    expect(busyMarker(rowOf(bubbleList, "Second"))).toBeNull();

    // A turn event alone says nothing about the list: only the server's history does.
    emit("ChatTurnStarted", { chatSessionId: "s2", turnId: "tB" });
    await settle();
    expect(busyMarker(rowOf(bubbleList, "Second"))).toBeNull();

    patchServer("s2", { isBusy: true });
    emit("ChatActivityChanged", { chatSessionId: "s2" });
    await waitFor(() => expect(busyMarker(rowOf(bubbleList, "Second"))).toBeTruthy());

    fireEvent.click(chatLink());
    await waitFor(() => expect(currentPath()).toBe("/chat/s1"));
    expect(await screen.findByText("s1 question")).toBeTruthy();
    // The open chat shows its own turn in the conversation, not as a marker.
    expect(busyMarker(rowOf(pageSidebar(), "First"))).toBeNull();
    expect(busyMarker(rowOf(pageSidebar(), "Second"))).toBeTruthy();

    patchServer("s2", { isBusy: false });
    emit("ChatActivityChanged", { chatSessionId: "s2" });
    await waitFor(() => expect(busyMarker(rowOf(pageSidebar(), "Second"))).toBeNull());
  });
});

describe("The open chat and what is shown", () => {
  test("a busy open chat keeps its marker while the bubble's list covers it", async () => {
    server = [
      summary("s1", "First", "2026-01-03T00:00:00Z", { isBusy: true }),
      summary("s2", "Second", "2026-01-02T00:00:00Z"),
    ];
    sessions.s1 = session("s1", "First", [msg("s1-0", 0, "user", "s1 question")], "tA");
    sessions.s2 = session("s2", "Second", [msg("s2-0", 0, "user", "s2 question")]);
    renderShell("/taskboard");

    fireEvent.click(await screen.findByRole("button", { name: "Open chat" }));
    await openChatFromList("First");
    expect(await screen.findByText("s1 question")).toBeTruthy();

    // The narrow panel's list now fills it: the chat's own indicator is out of sight.
    const list = await openChatList();
    expect(screen.queryByRole("status")).toBeNull();
    expect(busyMarker(rowOf(list, "First"))).toBeTruthy();
    expect(busyMarker(rowOf(list, "Second"))).toBeNull();
  });

  test("a chat opened by URL is not marked read while the page still shows the history loading", async () => {
    seedTwoChats();
    let releaseHistory: () => void = () => {};
    const historyArrives = new Promise<void>((resolve) => {
      releaseHistory = resolve;
    });
    chatService.listHistory.mockImplementation(async () => {
      await historyArrives;
      return server.map((c) => ({ ...c }));
    });
    renderShell("/chat/s1");

    await waitFor(() => expect(chatService.getById).toHaveBeenCalledWith("s1"));
    await settle();
    expect(screen.queryByText("s1 reply")).toBeNull();
    expect(chatService.markRead).not.toHaveBeenCalled();

    // jsdom has no scrolling: record where the transcript is asked to scroll.
    const scrolled: Element[] = [];
    HTMLElement.prototype.scrollTo = function (this: HTMLElement) {
      scrolled.push(this);
    } as typeof HTMLElement.prototype.scrollTo;
    try {
      releaseHistory();
      const reply = await screen.findByText("s1 reply");
      await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("s1", 1));
      // Shown at its newest message, not its oldest.
      await waitFor(() => expect(scrolled.some((el) => el.contains(reply))).toBe(true));
    } finally {
      delete (HTMLElement.prototype as Partial<HTMLElement>).scrollTo;
    }
  });

  test("a chat started with nothing in it yet is listed straight away", async () => {
    sessions.s9 = session("s9", "Fresh", []);
    chatService.start.mockImplementation(() => {
      server = [summary("s9", "Fresh", "2026-01-04T00:00:00Z")];
      return Promise.resolve(structuredClone(sessions.s9));
    });
    renderShell("/chat");

    const start = await screen.findByRole("button", { name: "Start chat" });
    await waitFor(() =>
      expect((screen.getByLabelText("AI provider") as HTMLSelectElement).value).toBe("p1"),
    );
    fireEvent.click(start);

    await waitFor(() => expect(currentPath()).toBe("/chat/s9"));
    expect(await within(pageSidebar()).findByText("Fresh")).toBeTruthy();
    expect(chatService.markRead).not.toHaveBeenCalled();
  });

  test("New chat in the bubble while its chat is still opening shows the start form", async () => {
    seedTwoChats();
    renderShell("/chat/s1");
    expect(await screen.findByText("s1 reply")).toBeTruthy();

    // From here on the chat never answers.
    chatService.getById.mockImplementation(() => new Promise<ChatSession>(() => {}));
    const readsBefore = chatService.getById.mock.calls.length;
    fireEvent.click(
      within(screen.getByRole("navigation")).getByRole("link", { name: "Taskboard" }),
    );
    fireEvent.click(await screen.findByRole("button", { name: "Open chat" }));
    await waitFor(() => expect(chatService.getById.mock.calls.length).toBeGreaterThan(readsBefore));

    fireEvent.click(within(await openChatList()).getByRole("button", { name: "New chat" }));

    expect(await screen.findByRole("button", { name: "Start chat" })).toBeTruthy();
    await waitFor(() =>
      expect((screen.getByLabelText("AI provider") as HTMLSelectElement).value).toBe("p1"),
    );
  });

  test("going back to the start form leaves the bubble with no chat open", async () => {
    sessions.s9 = session("s9", "Fresh", [msg("s9-0", 0, "assistant", "Hello there")]);
    chatService.start.mockImplementation(() => {
      server = [summary("s9", "Fresh", "2026-01-04T00:00:00Z")];
      return Promise.resolve(structuredClone(sessions.s9));
    });
    renderShell("/chat");

    const start = await screen.findByRole("button", { name: "Start chat" });
    await waitFor(() =>
      expect((screen.getByLabelText("AI provider") as HTMLSelectElement).value).toBe("p1"),
    );
    fireEvent.click(start);
    expect(await screen.findByText("Hello there")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "History back" }));
    await waitFor(() => expect(currentPath()).toBe("/chat"));
    expect(await screen.findByRole("button", { name: "Start chat" })).toBeTruthy();

    fireEvent.click(
      within(screen.getByRole("navigation")).getByRole("link", { name: "Taskboard" }),
    );
    fireEvent.click(await screen.findByRole("button", { name: "Open chat" }));
    const bubble = await screen.findByRole("dialog", { name: "AI chat" });
    expect(await within(bubble).findByRole("button", { name: "Start chat" })).toBeTruthy();
    await settle();
    expect(within(bubble).queryByText("Hello there")).toBeNull();
  });

  test("a chat the page cannot open is not left as the bubble's chat", async () => {
    seedTwoChats();
    renderShell("/chat/s1");
    expect(await screen.findByText("s1 reply")).toBeTruthy();

    // Still listed, but gone by the time it is opened.
    delete sessions.s2;
    fireEvent.click(within(pageSidebar()).getByText("Second"));
    expect(await screen.findByText("Chat not found.")).toBeTruthy();
    expect(await screen.findByRole("button", { name: "Start chat" })).toBeTruthy();

    fireEvent.click(
      within(screen.getByRole("navigation")).getByRole("link", { name: "Taskboard" }),
    );
    fireEvent.click(await screen.findByRole("button", { name: "Open chat" }));
    const bubble = await screen.findByRole("dialog", { name: "AI chat" });
    expect(await within(bubble).findByRole("button", { name: "Start chat" })).toBeTruthy();
    await settle();
    expect(within(bubble).queryByText("s1 reply")).toBeNull();
  });

  test("deleting a chat the page is still opening leaves for the start form, and the bubble has no chat", async () => {
    seedTwoChats();
    renderShell("/chat/s1");
    expect(await screen.findByText("s1 reply")).toBeTruthy();

    chatService.getById.mockImplementation((id: string) =>
      id === "s2"
        ? new Promise<ChatSession>(() => {})
        : Promise.resolve(structuredClone(sessions[id])),
    );
    fireEvent.click(within(pageSidebar()).getByText("Second"));
    await waitFor(() => expect(currentPath()).toBe("/chat/s2"));
    await waitFor(() => expect(chatService.getById).toHaveBeenCalledWith("s2"));

    showChatActions("Second");
    fireEvent.click(screen.getByRole("button", { name: "Delete chat Second" }));

    await waitFor(() => expect(currentPath()).toBe("/chat"));
    expect(await screen.findByRole("button", { name: "Start chat" })).toBeTruthy();
    expect(chatService.deleteOne).toHaveBeenCalledWith("s2");

    fireEvent.click(
      within(screen.getByRole("navigation")).getByRole("link", { name: "Taskboard" }),
    );
    fireEvent.click(await screen.findByRole("button", { name: "Open chat" }));
    const bubble = await screen.findByRole("dialog", { name: "AI chat" });
    expect(await within(bubble).findByRole("button", { name: "Start chat" })).toBeTruthy();
    await settle();
    expect(within(bubble).queryByText("s1 reply")).toBeNull();
  });

  test("a chat started on the page is listed even when the page is left before it is created", async () => {
    sessions.s9 = session("s9", "Fresh", []);
    let created: () => void = () => {};
    chatService.start.mockImplementation(
      () =>
        new Promise<ChatSession>((resolve) => {
          created = () => {
            server = [summary("s9", "Fresh", "2026-01-04T00:00:00Z")];
            resolve(structuredClone(sessions.s9));
          };
        }),
    );
    renderShell("/chat");

    const start = await screen.findByRole("button", { name: "Start chat" });
    await waitFor(() =>
      expect((screen.getByLabelText("AI provider") as HTMLSelectElement).value).toBe("p1"),
    );
    fireEvent.click(start);
    await waitFor(() => expect(chatService.start).toHaveBeenCalledTimes(1));

    fireEvent.click(
      within(screen.getByRole("navigation")).getByRole("link", { name: "Taskboard" }),
    );
    await waitFor(() => expect(currentPath()).toBe("/taskboard"));
    await act(async () => {
      created();
    });
    await settle();

    fireEvent.click(await screen.findByRole("button", { name: "Open chat" }));
    expect(await within(await openChatList()).findByText("Fresh")).toBeTruthy();
  });

  test("a chat whose first open failed opens when it is picked again", async () => {
    seedTwoChats();
    let firstTry = true;
    chatService.getById.mockImplementation((id: string) => {
      if (id === "s1" && firstTry) {
        firstTry = false;
        return Promise.reject({ message: "Could not reach the server." });
      }
      return Promise.resolve(structuredClone(sessions[id]));
    });
    renderShell("/chat/s1");
    expect(await screen.findByText("Could not reach the server.")).toBeTruthy();
    expect(await screen.findByRole("button", { name: "Start chat" })).toBeTruthy();

    fireEvent.click(within(pageSidebar()).getByText("First"));

    expect(await screen.findByText("s1 reply")).toBeTruthy();
    expect(currentPath()).toBe("/chat/s1");
    expect(screen.queryByText("Could not reach the server.")).toBeNull();
  });
});
