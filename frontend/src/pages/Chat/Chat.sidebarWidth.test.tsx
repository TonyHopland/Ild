import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router";
import type { AiProvider, ChatSessionSummary, User } from "../../types";
import { signalRHub } from "../../test-support";

const { chatService, aiProviderService, workItemService } = vi.hoisted(() => ({
  chatService: {
    listHistory: vi.fn(),
    getById: vi.fn(),
    start: vi.fn(),
    sendMessage: vi.fn(),
    interrupt: vi.fn(),
    deleteOne: vi.fn(),
    deleteMany: vi.fn(),
    searchChats: vi.fn(() => Promise.resolve([] as string[])),
    markRead: vi.fn(),
    rename: vi.fn(() => Promise.resolve()),
    setFavorite: vi.fn(() => Promise.resolve()),
  },
  aiProviderService: { getAll: vi.fn() },
  workItemService: { listEditProposalsFor: vi.fn(() => Promise.resolve([])) },
}));

vi.mock("../../hooks/useSignalR", async () =>
  (await import("../../test-support")).fakeSignalRModule(),
);
vi.mock("../../services/auth", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../services/auth")>()),
  chatService,
  aiProviderService,
  workItemService,
}));
vi.mock("../../utils/openLoopDocument", () => ({ getOpenLoopDocument: vi.fn() }));

import { ChatInboxProvider } from "../../components/ChatInbox";
import { AuthContext } from "../../hooks/useAuth";
import { WorkItemHubProvider } from "../../hooks/useWorkItemHub";
import { getPreference, setPreference } from "../../utils/userPreferences";
import ChatPage from ".";

const KEY = "chat.sidebar-width.page";
const BUBBLE_KEY = "chat.sidebar-width.bubble";

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

/** What the page measures; jsdom measures every box as 0. */
let containerWidth = 1000;
const observers = new Set<ResizeObserverCallback>();

beforeEach(() => {
  containerWidth = 1000;
  observers.clear();
  const chats: ChatSessionSummary[] = [
    {
      id: "s1",
      name: "First",
      createdAt: "2026-01-01T00:00:00Z",
      updatedAt: "2026-01-03T00:00:00Z",
      hasUnread: false,
    },
  ];
  chatService.listHistory.mockResolvedValue(chats);
  chatService.getById.mockResolvedValue({
    id: "s1",
    name: "First",
    aiProviderId: "p1",
    providerType: "claude-code",
    tools: ["ild"],
    createdAt: "2026-01-01T00:00:00Z",
    messages: [],
    activeTurnId: null,
  });
  aiProviderService.getAll.mockResolvedValue([provider]);
  vi.stubGlobal(
    "fetch",
    vi.fn(() => Promise.reject(new Error("offline"))),
  );
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(function (this: Element) {
    const width = this.classList.contains("chat-page") ? containerWidth : 0;
    return { x: 0, y: 0, top: 0, left: 0, right: width, bottom: 0, width, height: 0 } as DOMRect;
  });
  vi.stubGlobal(
    "ResizeObserver",
    class {
      private cb: ResizeObserverCallback;
      constructor(cb: ResizeObserverCallback) {
        this.cb = cb;
      }
      observe() {
        observers.add(this.cb);
        this.cb([], this as unknown as ResizeObserver);
      }
      unobserve() {}
      disconnect() {
        observers.delete(this.cb);
      }
    },
  );
});

afterEach(() => {
  cleanup();
  signalRHub.reset();
  vi.restoreAllMocks();
  vi.clearAllMocks();
  vi.unstubAllGlobals();
  localStorage.clear();
});

async function renderPage() {
  const auth = {
    user,
    token: "test-token",
    isAuthenticated: true,
    isLoading: false,
    login: vi.fn(),
    logout: vi.fn(),
  };
  render(
    <MemoryRouter initialEntries={["/chat"]}>
      <AuthContext.Provider value={auth}>
        <ChatInboxProvider>
          <Routes>
            <Route path="/chat" element={<ChatPage />} />
            <Route path="/chat/:chatId" element={<ChatPage />} />
          </Routes>
        </ChatInboxProvider>
      </AuthContext.Provider>
    </MemoryRouter>,
    { wrapper: WorkItemHubProvider },
  );
  await screen.findByRole("complementary", { name: "Chats" });
  await waitFor(() => expect(screen.queryByText("Loading…")).toBeNull());
}

const sidebar = () => screen.getByRole("complementary", { name: "Chats" });
const handle = () => screen.getByRole("separator", { name: "Resize chat list" });
const drawn = () => sidebar().style.width;

function drag(from: number, to: number) {
  fireEvent.pointerDown(handle(), { clientX: from, clientY: 0 });
  fireEvent.pointerMove(window, { clientX: to, clientY: 0 });
  fireEvent.pointerUp(window, { clientX: to, clientY: 0 });
}

describe("the chat list handle on the Chat page", () => {
  test("is a keyboard-reachable separator with the page's own limits, and the list keeps its CSS default until resized", async () => {
    await renderPage();

    const h = handle();
    expect(h.getAttribute("aria-orientation")).toBe("vertical");
    expect(h.tabIndex).toBe(0);
    expect(h.getAttribute("aria-valuenow")).toBe("288");
    expect(h.getAttribute("aria-valuemin")).toBe("240");
    expect(h.getAttribute("aria-valuemax")).toBe("680");
    expect(drawn()).toBe("");
  });

  test("dragging resizes live, saves on release under the page's key only, and respects the limits", async () => {
    setPreference(BUBBLE_KEY, "333");
    await renderPage();

    fireEvent.pointerDown(handle(), { clientX: 100, clientY: 0 });
    fireEvent.pointerMove(window, { clientX: 150, clientY: 0 });
    expect(drawn()).toBe("338px");
    expect(getPreference(KEY)).toBeUndefined();
    fireEvent.pointerUp(window, { clientX: 150, clientY: 0 });
    expect(getPreference(KEY)).toBe("338");
    expect(getPreference(BUBBLE_KEY)).toBe("333");

    drag(0, 5000);
    expect(drawn()).toBe("680px");
    drag(0, -5000);
    expect(drawn()).toBe("240px");
  });

  test("a press and release without movement saves nothing", async () => {
    await renderPage();

    drag(20, 20);

    expect(getPreference(KEY)).toBeUndefined();
    expect(drawn()).toBe("");
  });

  test("the arrow keys step by 16px from the default width and save each press", async () => {
    await renderPage();

    fireEvent.keyDown(handle(), { key: "ArrowRight" });
    expect(drawn()).toBe("304px");
    expect(getPreference(KEY)).toBe("304");
    fireEvent.keyDown(handle(), { key: "ArrowLeft" });
    fireEvent.keyDown(handle(), { key: "ArrowLeft" });
    expect(getPreference(KEY)).toBe("272");
  });

  test("double-clicking puts the list back to its default", async () => {
    setPreference(KEY, "400");
    await renderPage();
    expect(drawn()).toBe("400px");

    fireEvent.doubleClick(handle());

    expect(getPreference(KEY)).toBe("null");
    expect(drawn()).toBe("");
  });

  test("draws the stored width, shrinks to fit a narrower page and returns when it grows", async () => {
    setPreference(KEY, "600");
    await renderPage();
    expect(drawn()).toBe("600px");

    containerWidth = 700;
    act(() => observers.forEach((cb) => cb([], {} as ResizeObserver)));
    expect(drawn()).toBe("380px");

    containerWidth = 1000;
    act(() => observers.forEach((cb) => cb([], {} as ResizeObserver)));
    expect(drawn()).toBe("600px");
    expect(getPreference(KEY)).toBe("600");
  });

  test("a stored null leaves the default untouched", async () => {
    setPreference(KEY, "null");
    await renderPage();

    expect(drawn()).toBe("");
  });

  test("no handle, and no width on the list, on a phone", async () => {
    vi.stubGlobal("matchMedia", (query: string) => ({
      matches: query === "(max-width: 767px)",
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
    }));
    setPreference(KEY, "400");
    await renderPage();

    expect(screen.queryByRole("separator", { name: "Resize chat list" })).toBeNull();
    expect(drawn()).toBe("");
  });

  test("a drag cut off by the phone layout leaves no dragged width behind", async () => {
    let onChange: (e: MediaQueryListEvent) => void = () => {};
    vi.stubGlobal("matchMedia", (query: string) => ({
      matches: false,
      media: query,
      addEventListener: (_: string, cb: typeof onChange) => (onChange = cb),
      removeEventListener: () => {},
    }));
    await renderPage();

    fireEvent.pointerDown(handle(), { clientX: 100, clientY: 0 });
    fireEvent.pointerMove(window, { clientX: 200, clientY: 0 });
    expect(drawn()).toBe("388px");

    act(() => onChange({ matches: true } as MediaQueryListEvent));
    expect(screen.queryByRole("separator", { name: "Resize chat list" })).toBeNull();
    act(() => onChange({ matches: false } as MediaQueryListEvent));

    expect(drawn()).toBe("");
    expect(handle().className).not.toContain("is-dragging");
    expect(getPreference(KEY)).toBeUndefined();
  });
});
