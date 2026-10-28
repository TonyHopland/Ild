import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { screen, fireEvent, waitFor, within, act } from "@testing-library/react";
import { setPreference, getPreference, PreferenceKeys } from "../utils/userPreferences";
import type { AiProvider } from "../types";
import { openChatFromList, openChatList, signalRHub } from "../test-support";
import {
  chatMessage as msg,
  chatSession,
  chatSummary,
  emitChatEvent as emit,
  openResumedChat as openResumed,
  renderChatBubble,
  startNewChat,
  stubChatServices,
} from "../test-support.chatBubble";
import { FAB_POSITION_KEY, PANEL_POSITION_KEY, PANEL_SIZE_KEY } from "./chatPlacement";

const { getOpenLoopDocument, setCurrentChatSessionId } = vi.hoisted(() => ({
  getOpenLoopDocument: vi.fn(),
  setCurrentChatSessionId: vi.fn(),
}));

vi.mock("../hooks/useSignalR", async () => (await import("../test-support")).fakeSignalRModule());
vi.mock("../utils/openLoopDocument", () => ({ getOpenLoopDocument }));
vi.mock("../services/chatSessionStore", () => ({ setCurrentChatSessionId }));

const services = stubChatServices();
const { chatService, aiProviderService } = services;

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

afterEach(() => {
  signalRHub.reset();
  vi.clearAllMocks();
  // clearAllMocks keeps return-value implementations, so a per-test loop document
  // would leak into later tests; reset it back to "no loop open".
  getOpenLoopDocument.mockReset();
  localStorage.clear();
  // Restore the jsdom default viewport in case a test shrank it.
  window.innerWidth = 1024;
  window.innerHeight = 768;
});

describe("ChatBubble", () => {
  test("opening with no session prompts for a provider with ILD tools pre-checked", async () => {
    chatService.listHistory.mockResolvedValue([]);
    aiProviderService.getAll.mockResolvedValue([provider]);

    await renderChatBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));

    expect(await screen.findByText("Claude (claude-code)")).toBeTruthy();
    // `ild` is the only default-on entry; the toggle is labelled "ILD features".
    const ildBox = screen.getByLabelText("ILD features") as HTMLInputElement;
    expect(ildBox.checked).toBe(true);
    expect((screen.getByLabelText("Read") as HTMLInputElement).checked).toBe(false);
  });

  const PROVIDER_SELECTION_CASES: Array<{
    name: string;
    providers: AiProvider[];
    selected: string;
  }> = [
    {
      // The non-default is listed first to prove selection follows isDefault, not order.
      name: "pre-selects the default provider in the dropdown",
      providers: [{ ...provider, id: "p0", name: "GPT", isDefault: false }, provider],
      selected: "p1",
    },
    {
      name: "leaves the dropdown unselected when no provider is the default",
      providers: [
        { ...provider, id: "p0", name: "GPT", isDefault: false },
        { ...provider, id: "p2", name: "Llama", isDefault: false },
      ],
      selected: "",
    },
  ];

  test.each(PROVIDER_SELECTION_CASES)("$name", async ({ providers, selected }) => {
    chatService.listHistory.mockResolvedValue([]);
    aiProviderService.getAll.mockResolvedValue(providers);

    await renderChatBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));

    const select = (await screen.findByLabelText("AI provider")) as HTMLSelectElement;
    expect(select.value).toBe(selected);
  });

  const CHAT_CONTEXT_CASES: Array<{
    name: string;
    path: string;
    liveLoop: object | null;
    text: string;
    workItemId: string | null;
  }> = [
    {
      name: "sends the open work item id from the route as Chat Context",
      path: "/taskboard/wi-77",
      liveLoop: null,
      text: "look at this",
      workItemId: "wi-77",
    },
    {
      name: "sends the open Loop Editor's live document with the message",
      path: "/",
      liveLoop: { $schema: "ild-loop-template/v2", name: "My Loop", nodes: [], edges: [] },
      text: "edit the loop",
      workItemId: null,
    },
    {
      name: "sends a null Chat Context when no work item is open",
      path: "/taskboard",
      liveLoop: null,
      text: "general question",
      workItemId: null,
    },
  ];

  test.each(CHAT_CONTEXT_CASES)("$name", async ({ path, liveLoop, text, workItemId }) => {
    chatService.sendMessage.mockResolvedValue(null);
    if (liveLoop) getOpenLoopDocument.mockReturnValue(liveLoop);
    await openResumed(chatSession(), path);

    const input = await screen.findByLabelText("Chat message");
    fireEvent.change(input, { target: { value: text } });
    fireEvent.click(screen.getByText("Send"));

    await waitFor(() =>
      expect(chatService.sendMessage).toHaveBeenCalledWith(
        "s1",
        text,
        workItemId,
        liveLoop ? JSON.stringify(liveLoop) : null,
      ),
    );
  });

  test("the message box goes back to one line once its message is sent", async () => {
    chatService.sendMessage.mockResolvedValue(null);
    aiProviderService.getAll.mockResolvedValue([provider]);
    await openResumed(chatSession(), "/taskboard");

    const input = (await screen.findByLabelText("Chat message")) as HTMLTextAreaElement;
    fireEvent.change(input, { target: { value: "a\nlong\nmessage" } });
    input.style.height = "120px";
    fireEvent.keyDown(input, { key: "Enter" });

    await waitFor(() => expect(chatService.sendMessage).toHaveBeenCalled());
    expect(input.style.height).toBe("");
  });

  test("Enter sends the message and Shift+Enter does not", async () => {
    chatService.sendMessage.mockResolvedValue(null);
    aiProviderService.getAll.mockResolvedValue([provider]);
    await openResumed(chatSession(), "/taskboard");

    const input = await screen.findByLabelText("Chat message");
    expect(input.tagName).toBe("TEXTAREA");
    fireEvent.change(input, { target: { value: "line one" } });
    fireEvent.keyDown(input, { key: "Enter", shiftKey: true });
    expect(chatService.sendMessage).not.toHaveBeenCalled();

    fireEvent.keyDown(input, { key: "Enter" });
    await waitFor(() =>
      expect(chatService.sendMessage).toHaveBeenCalledWith("s1", "line one", null, null),
    );
  });

  test("starting a chat locks in the session and reveals the input box", async () => {
    chatService.listHistory.mockResolvedValue([]);
    aiProviderService.getAll.mockResolvedValue([provider]);
    chatService.start.mockResolvedValue(chatSession({ name: null }));
    // Joining the new chat's live stream re-reads its state from the server.
    chatService.getById.mockResolvedValue(chatSession({ name: null }));

    await renderChatBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));
    fireEvent.change(await screen.findByLabelText("AI provider"), { target: { value: "p1" } });
    fireEvent.click(screen.getByText("Start chat"));

    const input = await screen.findByLabelText("Chat message");
    expect(input).toBeTruthy();
    expect(chatService.start).toHaveBeenCalledWith("p1", ["ild"]);

    // A chat that has never run a turn is idle: nothing to stop, nothing working.
    expect(screen.queryByLabelText("Stop")).toBeNull();
    expect(screen.queryByRole("status")).toBeNull();
  });

  test("resuming a past chat rehydrates its transcript", async () => {
    await openResumed(
      chatSession({
        messages: [
          msg({ id: "m1", role: "user", content: "hello", sequence: 0 }),
          msg({ id: "m2", role: "assistant", content: "hi back", sequence: 1 }),
        ],
      }),
    );

    expect(await screen.findByText("hello")).toBeTruthy();
    expect(screen.getByText("hi back")).toBeTruthy();
    expect(chatService.getById).toHaveBeenCalledWith("s1");
  });

  test("a chat's actions open one row at a time and close on Escape, without it reaching the page below, or on a click outside", async () => {
    chatService.listHistory.mockResolvedValue([chatSummary("s1", "One"), chatSummary("s2", "Two")]);
    aiProviderService.getAll.mockResolvedValue([provider]);
    const pageEscape = vi.fn();
    const onPageKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") pageEscape();
    };
    document.addEventListener("keydown", onPageKey);
    try {
      await renderChatBubble();
      fireEvent.click(await screen.findByLabelText("Open chat"));
      await openChatList();
      await screen.findByText("One");
      const actions = (name: string) =>
        screen.getByRole("button", { name: `Actions for chat ${name}` });

      expect(screen.queryByLabelText("Rename chat One")).toBeNull();
      fireEvent.click(actions("One"));
      expect(actions("One").getAttribute("aria-expanded")).toBe("true");
      expect(screen.getByLabelText("Rename chat One")).toBeTruthy();

      fireEvent.click(actions("Two"));
      expect(screen.queryByLabelText("Rename chat One")).toBeNull();
      expect(screen.getByLabelText("Delete chat Two")).toBeTruthy();

      fireEvent.keyDown(screen.getByLabelText("Delete chat Two"), { key: "Escape" });
      expect(screen.queryByLabelText("Delete chat Two")).toBeNull();
      expect(actions("Two").getAttribute("aria-expanded")).toBe("false");
      expect(pageEscape).not.toHaveBeenCalled();

      fireEvent.click(actions("One"));
      fireEvent.pointerDown(screen.getByLabelText("Search chats"));
      expect(screen.queryByLabelText("Rename chat One")).toBeNull();
      expect(chatService.deleteOne).not.toHaveBeenCalled();
    } finally {
      document.removeEventListener("keydown", onPageKey);
    }
  });

  // The list scrolls and clips, so the actions of a row near its bottom would open
  // out of sight. jsdom does no layout and has no scrollIntoView, so stub it to
  // observe that an opened menu is brought into view.
  test("a chat's opened actions are scrolled into view within the list", async () => {
    chatService.listHistory.mockResolvedValue([chatSummary("s1", "Last row")]);
    aiProviderService.getAll.mockResolvedValue([provider]);
    const scrolled: Element[] = [];
    const scrollIntoView = vi.fn(function (this: Element) {
      scrolled.push(this);
    });
    const original = Object.getOwnPropertyDescriptor(Element.prototype, "scrollIntoView");
    Element.prototype.scrollIntoView = scrollIntoView;
    try {
      await renderChatBubble();
      fireEvent.click(await screen.findByLabelText("Open chat"));
      await openChatList();
      fireEvent.click(await screen.findByRole("button", { name: "Actions for chat Last row" }));

      const menu = screen.getByRole("group", { name: "Actions for chat Last row" });
      expect(scrollIntoView).toHaveBeenCalledWith({ block: "nearest" });
      expect(scrolled).toContain(menu);
    } finally {
      if (original) Object.defineProperty(Element.prototype, "scrollIntoView", original);
      else delete (Element.prototype as { scrollIntoView?: unknown }).scrollIntoView;
    }
  });

  test("a failed message search with no title match says so, not that nothing matched", async () => {
    chatService.listHistory.mockResolvedValue([chatSummary("s1", "Deploy loop")]);
    aiProviderService.getAll.mockResolvedValue([provider]);
    let fail!: (reason: unknown) => void;
    chatService.searchChats.mockImplementation(
      () =>
        new Promise<string[]>((_, reject) => {
          fail = reject;
        }),
    );
    await renderChatBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));
    const list = await openChatList();

    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval"] });
    try {
      fireEvent.change(within(list).getByLabelText("Search chats"), {
        target: { value: "broken" },
      });
      expect(within(list).getByText("Searching…")).toBeTruthy();
      act(() => {
        vi.advanceTimersByTime(1000);
      });
      expect(chatService.searchChats).toHaveBeenCalledWith("broken");
      expect(within(list).getByText("Searching…")).toBeTruthy();

      await act(async () => fail(new Error("search failed")));
      expect(within(list).getByText("Message search failed")).toBeTruthy();
      expect(within(list).queryByText("Searching…")).toBeNull();
      expect(within(list).queryByText("No chats match")).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });

  test("New chat leaves the open chat for the start form and retains it (no delete)", async () => {
    await openResumed(chatSession({ name: "Retained chat" }));

    await startNewChat();

    // The start form is shown and the chat was not deleted.
    expect(await screen.findByText("Start chat")).toBeTruthy();
    expect(screen.queryByLabelText("Chat message")).toBeNull();
    expect(chatService.deleteOne).not.toHaveBeenCalled();
    expect(chatService.deleteMany).not.toHaveBeenCalled();
  });

  test("an unsent draft stays with its chat: another chat and New chat start empty", async () => {
    chatService.listHistory.mockResolvedValue([
      chatSummary("s1", "First chat"),
      chatSummary("s2", "Other chat"),
    ]);
    chatService.getById.mockImplementation((id: string) =>
      Promise.resolve(chatSession({ id, name: id === "s1" ? "First chat" : "Other chat" })),
    );
    aiProviderService.getAll.mockResolvedValue([provider]);
    await renderChatBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));
    const draft = () => screen.getByLabelText("Chat message") as HTMLInputElement;

    await openChatFromList("First chat");
    await screen.findByLabelText("Chat message");
    fireEvent.change(draft(), { target: { value: "meant for the first chat" } });

    await openChatFromList("Other chat");
    await waitFor(() => expect(screen.getByText("Other chat")).toBeTruthy());
    expect(draft().value).toBe("");
    fireEvent.change(draft(), { target: { value: "meant for the other chat" } });

    await startNewChat();
    expect(await screen.findByText("Start chat")).toBeTruthy();
    await openChatFromList("Other chat");
    await screen.findByLabelText("Chat message");
    expect(draft().value).toBe("");
    expect(chatService.sendMessage).not.toHaveBeenCalled();
  });

  test("reloads providers when ending a chat if the first load failed", async () => {
    chatService.listHistory.mockResolvedValue([chatSummary("s1", "Old chat")]);
    chatService.getById.mockResolvedValue(chatSession({ id: "s1", name: "Old chat" }));
    // The first provider load fails (e.g. a transient auth hiccup right at
    // startup); the retry when the start form re-appears succeeds.
    aiProviderService.getAll.mockRejectedValueOnce(new Error("boom")).mockResolvedValue([provider]);

    await renderChatBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));

    // Resume the past chat (this does not need providers), then end it.
    await openChatFromList("Old chat");
    await screen.findByLabelText("Chat message");
    await startNewChat();

    // The provider list fills without having to close and reopen the panel.
    expect(await screen.findByText("Claude (claude-code)")).toBeTruthy();
  });

  test("publishes the chat session id so the loop editor can join the same group", async () => {
    await openResumed(chatSession());

    // The resumed session is published for other components (the LoopEditor).
    await waitFor(() => expect(setCurrentChatSessionId).toHaveBeenCalledWith("s1"));

    // Leaving publishes null so the editor leaves the group.
    await startNewChat();
    await waitFor(() => expect(setCurrentChatSessionId).toHaveBeenCalledWith(null));
  });

  test("a stop that loses the race to the turn finishing is swallowed", async () => {
    chatService.sendMessage.mockResolvedValue(null);
    // The turn completed between render and click, so the chat no longer has one.
    chatService.interrupt.mockRejectedValue(new Error("Chat not found."));
    await openResumed(chatSession());
    chatService.getById.mockResolvedValue(chatSession({ activeTurnId: "t1" }));

    const input = await screen.findByLabelText("Chat message");
    fireEvent.change(input, { target: { value: "long task" } });
    fireEvent.click(screen.getByText("Send"));

    fireEvent.click(await screen.findByLabelText("Stop"));

    await waitFor(() => expect(chatService.interrupt).toHaveBeenCalledWith("s1"));
    // No error surfaced, and the button is still live for another attempt.
    expect(screen.queryByText("Chat not found.")).toBeNull();
    await waitFor(() =>
      expect((screen.getByLabelText("Stop") as HTMLButtonElement).disabled).toBe(false),
    );
  });

  test("a second click cannot fire a stop while the first is in flight", async () => {
    chatService.sendMessage.mockResolvedValue(null);
    let release!: () => void;
    chatService.interrupt.mockReturnValue(
      new Promise<void>((resolve) => {
        release = resolve;
      }),
    );
    await openResumed(chatSession());
    chatService.getById.mockResolvedValue(chatSession({ activeTurnId: "t1" }));

    const input = await screen.findByLabelText("Chat message");
    fireEvent.change(input, { target: { value: "long task" } });
    fireEvent.click(screen.getByText("Send"));

    fireEvent.click(await screen.findByLabelText("Stop"));
    await waitFor(() =>
      expect((screen.getByLabelText("Stop") as HTMLButtonElement).disabled).toBe(true),
    );

    fireEvent.click(screen.getByLabelText("Stop"));
    expect(chatService.interrupt).toHaveBeenCalledTimes(1);

    await act(async () => {
      release();
    });
  });

  test("streams a turn and flags an interrupted partial reply", async () => {
    // Resumed while t1 is streaming, which is how a client comes to be shown one.
    await openResumed(chatSession({ activeTurnId: "t1" }));
    await screen.findByLabelText("Chat message");

    // Live streaming delta appears, then a finalized interrupted reply replaces it.
    emit("ChatTurnProgress", { chatSessionId: "s1", turnId: "t1", delta: "partial" });
    expect(await screen.findByText("partial")).toBeTruthy();

    emit("ChatMessageAppended", {
      chatSessionId: "s1",
      turnId: "t1",
      message: msg({
        id: "a1",
        role: "assistant",
        content: "partial",
        interrupted: true,
        sequence: 1,
      }),
    });

    await waitFor(() => expect(screen.getByText("interrupted")).toBeTruthy());

    // The turn that was interrupted then reports itself finished, under the id the
    // client is watching, so this is the completion that ends the turn: the stop
    // button and the working indicator go with it.
    expect(screen.queryByLabelText("Stop")).toBeTruthy();
    emit("ChatTurnCompleted", { chatSessionId: "s1", turnId: "t1", interrupted: true });
    expect(screen.queryByLabelText("Stop")).toBeNull();
    expect(screen.queryByRole("status")).toBeNull();
    expect(screen.getByText("interrupted")).toBeTruthy();
  });
});

describe("ChatBubble placement", () => {
  test("renders nothing when chat is disabled in settings", async () => {
    setPreference(PreferenceKeys.ChatEnabled, "false");
    chatService.listHistory.mockResolvedValue([]);

    const { container } = await renderChatBubble();
    expect(screen.queryByLabelText("Open chat")).toBeNull();
    expect(container.firstChild).toBeNull();
  });

  test("on a phone the icon sits in its corner above the tab bar, whatever spot was saved, and does not drag", async () => {
    vi.stubGlobal("matchMedia", (query: string) => ({
      matches: query === "(max-width: 767px)",
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
    }));
    setPreference(FAB_POSITION_KEY, JSON.stringify({ x: 100, y: 100 }));
    chatService.listHistory.mockResolvedValue([]);
    aiProviderService.getAll.mockResolvedValue([provider]);

    await renderChatBubble();
    const fab = (await screen.findByLabelText("Open chat")) as HTMLElement;

    // 1024×768 less the 60px tab bar: the corner is (952, 636).
    expect([fab.style.left, fab.style.top]).toEqual(["952px", "636px"]);
    fireEvent.pointerDown(fab, { clientX: 500, clientY: 500 });
    fireEvent.pointerMove(window, { clientX: 450, clientY: 450 });
    fireEvent.pointerUp(window, { clientX: 450, clientY: 450 });
    expect([fab.style.left, fab.style.top]).toEqual(["952px", "636px"]);
    expect(JSON.parse(getPreference(FAB_POSITION_KEY)!)).toEqual({ x: 100, y: 100 });
    vi.unstubAllGlobals();
  });

  test("dragging the icon moves it, persists the spot, and suppresses the open click", async () => {
    chatService.listHistory.mockResolvedValue([]);
    aiProviderService.getAll.mockResolvedValue([provider]);

    await renderChatBubble();
    const fab = await screen.findByLabelText("Open chat");

    fireEvent.pointerDown(fab, { clientX: 500, clientY: 500 });
    fireEvent.pointerMove(window, { clientX: 450, clientY: 450 });
    fireEvent.pointerUp(window, { clientX: 450, clientY: 450 });

    // jsdom is 1024×768, so the default corner is (952, 696); a -50/-50 drag
    // lands at (902, 646), still inside the viewport.
    expect((fab as HTMLElement).style.left).toBe("902px");
    expect((fab as HTMLElement).style.top).toBe("646px");
    expect(JSON.parse(getPreference(FAB_POSITION_KEY) ?? "{}")).toEqual({ x: 902, y: 646 });

    // The click that ends the drag must not open the panel.
    fireEvent.click(fab);
    expect(screen.queryByLabelText("AI provider")).toBeNull();
    expect(screen.getByLabelText("Open chat")).toBeTruthy();
  });

  test("resizing the panel updates and persists its size", async () => {
    chatService.listHistory.mockResolvedValue([]);
    aiProviderService.getAll.mockResolvedValue([provider]);

    await renderChatBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));

    const panel = await screen.findByRole("dialog", { name: "AI chat" });
    const handle = screen.getByLabelText("Resize chat");

    fireEvent.pointerDown(handle, { clientX: 0, clientY: 0 });
    fireEvent.pointerMove(window, { clientX: 100, clientY: 80 });
    fireEvent.pointerUp(window, { clientX: 100, clientY: 80 });

    // Default size is 384×512; +100/+80 grows it to 484×592.
    expect((panel as HTMLElement).style.width).toBe("484px");
    expect((panel as HTMLElement).style.height).toBe("592px");
    expect(JSON.parse(getPreference(PANEL_SIZE_KEY) ?? "{}")).toEqual({
      width: 484,
      height: 592,
    });
  });

  test("a window resize draws the icon in view but keeps the spot the user chose", async () => {
    setPreference(FAB_POSITION_KEY, JSON.stringify({ x: 900, y: 700 }));
    chatService.listHistory.mockResolvedValue([]);

    await renderChatBubble();
    const fab = await screen.findByLabelText("Open chat");
    expect((fab as HTMLElement).style.left).toBe("900px");

    act(() => {
      window.innerWidth = 400;
      window.innerHeight = 400;
      window.dispatchEvent(new Event("resize"));
    });

    // (400 - 52 - 20) = 328 is the furthest the icon can sit.
    expect((fab as HTMLElement).style.left).toBe("328px");
    expect((fab as HTMLElement).style.top).toBe("328px");
    // The spot the user chose is not overwritten by a window that is small for a moment…
    expect(JSON.parse(getPreference(FAB_POSITION_KEY) ?? "{}")).toEqual({ x: 900, y: 700 });

    act(() => {
      window.innerWidth = 1024;
      window.innerHeight = 768;
      window.dispatchEvent(new Event("resize"));
    });

    // …so the icon goes back to it when the window grows again.
    expect((fab as HTMLElement).style.left).toBe("900px");
    expect((fab as HTMLElement).style.top).toBe("696px");
  });

  test("dragging the window header moves and persists the panel position", async () => {
    chatService.listHistory.mockResolvedValue([]);
    aiProviderService.getAll.mockResolvedValue([provider]);

    await renderChatBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));

    const panel = await screen.findByRole("dialog", { name: "AI chat" });
    const header = screen.getByText("AI Chat").closest(".chat-panel-header") as HTMLElement;

    // The panel starts anchored to the default-corner icon, clamped to (620, 236).
    expect((panel as HTMLElement).style.left).toBe("620px");
    expect((panel as HTMLElement).style.top).toBe("236px");

    fireEvent.pointerDown(header, { clientX: 200, clientY: 200 });
    fireEvent.pointerMove(window, { clientX: 120, clientY: 140 });
    fireEvent.pointerUp(window, { clientX: 120, clientY: 140 });

    // -80/-60 from the (620, 236) anchor lands at (540, 176), still on-screen.
    expect((panel as HTMLElement).style.left).toBe("540px");
    expect((panel as HTMLElement).style.top).toBe("176px");
    expect(JSON.parse(getPreference(PANEL_POSITION_KEY) ?? "{}")).toEqual({
      x: 540,
      y: 176,
    });
  });

  test("a header button press does not drag the window", async () => {
    await openResumed(chatSession());

    const panel = await screen.findByRole("dialog", { name: "AI chat" });
    const chatList = screen.getByRole("button", { name: "Chat list" });

    // Pressing and moving on a header button must not reposition the panel…
    fireEvent.pointerDown(chatList, { clientX: 200, clientY: 200 });
    fireEvent.pointerMove(window, { clientX: 50, clientY: 50 });
    fireEvent.pointerUp(window, { clientX: 50, clientY: 50 });

    expect((panel as HTMLElement).style.left).toBe("620px");
    expect((panel as HTMLElement).style.top).toBe("236px");
    expect(getPreference(PANEL_POSITION_KEY)).toBeUndefined();

    // …and the button still does its job, showing the chat list.
    fireEvent.click(chatList);
    expect(await screen.findByRole("complementary", { name: "Chats" })).toBeTruthy();
  });
});
