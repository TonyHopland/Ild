import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { act, cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { PANEL_SIZE_KEY } from "./chatPlacement";
import { signalRHub } from "../test-support";
import {
  chatServer,
  chatSummary,
  openChatPanel,
  renderChatBubble,
  stubChatServices,
} from "../test-support.chatBubble";
import { getPreference, setPreference } from "../utils/userPreferences";

vi.mock("../hooks/useSignalR", async () => (await import("../test-support")).fakeSignalRModule());
vi.mock("../utils/openLoopDocument", () => ({ getOpenLoopDocument: vi.fn() }));
vi.mock("../services/chatSessionStore", () => ({ setCurrentChatSessionId: vi.fn() }));

const server = chatServer(stubChatServices());

const KEY = "chat.sidebar-width.bubble";
const PAGE_KEY = "chat.sidebar-width.page";

/** What the panel's main area measures; jsdom measures every box as 0. */
let containerWidth = 800;
const observers = new Set<ResizeObserverCallback>();

beforeEach(() => {
  containerWidth = 800;
  observers.clear();
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(function (this: Element) {
    const width = this.classList.contains("chat-panel-main") ? containerWidth : 0;
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
  server.chats = [chatSummary("a", "Alpha")];
  setPreference(PANEL_SIZE_KEY, JSON.stringify({ width: 800, height: 600 }));
});

afterEach(() => {
  cleanup();
  signalRHub.reset();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
  localStorage.clear();
});

async function openPanel() {
  await renderChatBubble();
  await openChatPanel();
  await waitFor(() => expect(screen.queryByText("Loading…")).toBeNull());
}

const sidebar = () => screen.getByRole("complementary", { name: "Chats" });
const handle = () => screen.getByRole("separator", { name: "Resize chat list" });
const drawn = () => sidebar().style.width;
const toggle = () => screen.getByRole("button", { name: "Chat list" });

function drag(from: number, to: number) {
  fireEvent.pointerDown(handle(), { clientX: from, clientY: 0 });
  fireEvent.pointerMove(window, { clientX: to, clientY: 0 });
  fireEvent.pointerUp(window, { clientX: to, clientY: 0 });
}

/** The panel's main area changes size, as when the user resizes the panel. */
function resizeContainer(width: number) {
  containerWidth = width;
  act(() => observers.forEach((cb) => cb([], {} as ResizeObserver)));
}

describe("the chat list handle in the bubble", () => {
  test("sits on the list's edge as a keyboard-reachable separator, and the list keeps its CSS default until resized", async () => {
    await openPanel();

    const h = handle();
    expect(h.getAttribute("aria-orientation")).toBe("vertical");
    expect(h.tabIndex).toBe(0);
    expect(h.getAttribute("aria-valuenow")).toBe("240");
    expect(h.getAttribute("aria-valuemin")).toBe("240");
    expect(h.getAttribute("aria-valuemax")).toBe("480");
    expect(drawn()).toBe("");
  });

  test("dragging resizes live by the pointer's movement and saves only when released", async () => {
    await openPanel();

    fireEvent.pointerDown(handle(), { clientX: 100, clientY: 0 });
    fireEvent.pointerMove(window, { clientX: 160, clientY: 0 });
    expect(drawn()).toBe("300px");
    expect(handle().getAttribute("aria-valuenow")).toBe("300");
    expect(getPreference(KEY)).toBeUndefined();

    fireEvent.pointerUp(window, { clientX: 160, clientY: 0 });
    expect(getPreference(KEY)).toBe("300");
    expect(drawn()).toBe("300px");

    // Later pointer movement is no longer a drag.
    fireEvent.pointerMove(window, { clientX: 400, clientY: 0 });
    expect(drawn()).toBe("300px");
  });

  test("a second drag starts from the width drawn at the press, and stays within the limits", async () => {
    await openPanel();

    drag(0, 100);
    expect(getPreference(KEY)).toBe("340");
    drag(0, 5000);
    expect(drawn()).toBe("480px");
    expect(getPreference(KEY)).toBe("480");
    drag(0, -5000);
    expect(drawn()).toBe("240px");
    expect(getPreference(KEY)).toBe("240");
  });

  test("a press and release without movement saves nothing", async () => {
    await openPanel();

    drag(50, 50);

    expect(getPreference(KEY)).toBeUndefined();
    expect(drawn()).toBe("");
  });

  test("the arrow keys step by 16px from the drawn width, within the limits, and save each press", async () => {
    await openPanel();

    fireEvent.keyDown(handle(), { key: "ArrowRight" });
    expect(drawn()).toBe("256px");
    expect(getPreference(KEY)).toBe("256");
    fireEvent.keyDown(handle(), { key: "ArrowRight" });
    expect(getPreference(KEY)).toBe("272");
    fireEvent.keyDown(handle(), { key: "ArrowLeft" });
    expect(getPreference(KEY)).toBe("256");

    fireEvent.keyDown(handle(), { key: "Home" });
    fireEvent.keyDown(handle(), { key: "ArrowUp" });
    expect(getPreference(KEY)).toBe("256");

    for (let i = 0; i < 30; i++) fireEvent.keyDown(handle(), { key: "ArrowLeft" });
    expect(drawn()).toBe("240px");
    expect(getPreference(KEY)).toBe("240");
    for (let i = 0; i < 30; i++) fireEvent.keyDown(handle(), { key: "ArrowRight" });
    expect(drawn()).toBe("480px");
    expect(getPreference(KEY)).toBe("480");
  });

  test("double-clicking puts the list back to its default and reads as never resized", async () => {
    setPreference(KEY, "400");
    await openPanel();
    expect(drawn()).toBe("400px");

    fireEvent.doubleClick(handle());

    expect(getPreference(KEY)).toBe("null");
    expect(drawn()).toBe("");
    expect(handle().getAttribute("aria-valuenow")).toBe("240");
  });

  test("draws the width stored for the bubble, not the page's", async () => {
    setPreference(KEY, "400");
    setPreference(PAGE_KEY, "300");
    await openPanel();
    expect(drawn()).toBe("400px");

    drag(0, 20);
    expect(getPreference(KEY)).toBe("420");
    expect(getPreference(PAGE_KEY)).toBe("300");
  });

  test.each(["null", "not json{", '"wide"'])(
    "a stored %s leaves the default untouched",
    async (raw) => {
      setPreference(KEY, raw);
      await openPanel();

      expect(drawn()).toBe("");
      expect(getPreference(KEY)).toBe(raw);
    },
  );

  test("hiding and showing the list brings it back at the chosen width", async () => {
    await openPanel();
    drag(0, 100);
    expect(drawn()).toBe("340px");

    fireEvent.click(toggle());
    expect(screen.queryByRole("complementary", { name: "Chats" })).toBeNull();
    expect(screen.queryByRole("separator", { name: "Resize chat list" })).toBeNull();
    fireEvent.click(toggle());

    expect(drawn()).toBe("340px");
  });

  test("shrinks to fit a smaller panel and returns to the chosen width when it grows, never writing the preference", async () => {
    setPreference(KEY, "400");
    await openPanel();
    expect(drawn()).toBe("400px");

    resizeContainer(600);
    expect(drawn()).toBe("280px");
    expect(handle().getAttribute("aria-valuemax")).toBe("280");

    resizeContainer(400);
    expect(drawn()).toBe("240px");

    resizeContainer(800);
    expect(drawn()).toBe("400px");
    expect(getPreference(KEY)).toBe("400");
  });

  test("a drag in a shrunken panel is limited by its size", async () => {
    await openPanel();
    resizeContainer(600);

    drag(0, 1000);

    expect(getPreference(KEY)).toBe("280");
  });

  test("hiding the list mid-drag removes the drag: nothing more is drawn or saved", async () => {
    await openPanel();
    fireEvent.pointerDown(handle(), { clientX: 0, clientY: 0 });
    fireEvent.pointerMove(window, { clientX: 50, clientY: 0 });

    fireEvent.click(toggle());
    fireEvent.pointerMove(window, { clientX: 90, clientY: 0 });
    fireEvent.pointerUp(window, { clientX: 90, clientY: 0 });

    expect(getPreference(KEY)).toBeUndefined();
    fireEvent.click(toggle());
    expect(drawn()).toBe("");
  });

  test("no handle, and no width on the list, while the list fills a narrow panel", async () => {
    setPreference(PANEL_SIZE_KEY, JSON.stringify({ width: 500, height: 600 }));
    setPreference(KEY, "400");
    await renderChatBubble();
    await openChatPanel();
    await waitFor(() => expect(screen.queryByText("Loading…")).toBeNull());
    fireEvent.click(toggle());

    expect(sidebar().classList.contains("chat-sidebar-full")).toBe(true);
    expect(screen.queryByRole("separator", { name: "Resize chat list" })).toBeNull();
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
    await renderChatBubble();
    await openChatPanel();
    await waitFor(() => expect(screen.queryByText("Loading…")).toBeNull());
    if (!screen.queryByRole("complementary", { name: "Chats" })) fireEvent.click(toggle());

    expect(screen.queryByRole("separator", { name: "Resize chat list" })).toBeNull();
    expect(drawn()).toBe("");
  });
});
