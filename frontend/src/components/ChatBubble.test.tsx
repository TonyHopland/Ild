import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor, act } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import type { AiProvider, ChatMessage, ChatSession, ChatSessionSummary } from "../types";
import { FAB_POSITION_KEY, PANEL_POSITION_KEY, PANEL_SIZE_KEY } from "./chatPlacement";
import { CHAT_ENABLED_KEY } from "../hooks/useChatEnabled";

// Hoisted so the vi.mock factories (which are hoisted to the top) can reference
// them without a temporal-dead-zone error.
const { handlers, chatService, aiProviderService, getOpenLoopDocument, setCurrentChatSessionId } =
  vi.hoisted(() => ({
    handlers: {} as Record<string, (msg: { payload: unknown }) => void>,
    chatService: {
      listHistory: vi.fn(),
      getById: vi.fn(),
      start: vi.fn(),
      sendMessage: vi.fn(),
      interrupt: vi.fn(),
      deleteOne: vi.fn(),
      deleteAll: vi.fn(),
    },
    aiProviderService: {
      getAll: vi.fn(),
    },
    getOpenLoopDocument: vi.fn(),
    setCurrentChatSessionId: vi.fn(),
  }));

vi.mock("../hooks/useSignalR", () => ({
  useSignalR: () => ({
    connectionState: "connected",
    on: (event: string, handler: (msg: { payload: unknown }) => void) => {
      handlers[event] = handler;
    },
    off: (event: string) => {
      delete handlers[event];
    },
    invoke: vi.fn(() => Promise.resolve()),
  }),
}));

vi.mock("../services/auth", () => ({ chatService, aiProviderService }));
vi.mock("../utils/openLoopDocument", () => ({ getOpenLoopDocument }));
vi.mock("../services/chatSessionStore", () => ({ setCurrentChatSessionId }));

import ChatBubble from "./ChatBubble";

// The bubble reads the open work item from the route (useMatch), so every render
// must sit inside a Router. `initialPath` lets a test simulate having a work item
// open (e.g. "/taskboard/wi-77").
function renderBubble(initialPath = "/") {
  return render(
    <MemoryRouter initialEntries={[initialPath]}>
      <ChatBubble />
    </MemoryRouter>,
  );
}

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

function msg(partial: Partial<ChatMessage>): ChatMessage {
  return {
    id: partial.id ?? crypto.randomUUID(),
    role: partial.role ?? "assistant",
    content: partial.content ?? "",
    interrupted: partial.interrupted ?? false,
    sequence: partial.sequence ?? 0,
    createdAt: "2026-01-01T00:00:00Z",
  };
}

function chatSession(partial: Partial<ChatSession> = {}): ChatSession {
  return {
    id: partial.id ?? "s1",
    name: partial.name ?? "Past chat",
    aiProviderId: partial.aiProviderId ?? "p1",
    providerType: partial.providerType ?? "claude-code",
    tools: partial.tools ?? ["ild"],
    createdAt: partial.createdAt ?? "2026-01-01T00:00:00Z",
    messages: partial.messages ?? [],
  };
}

function summary(partial: Partial<ChatSessionSummary> = {}): ChatSessionSummary {
  return {
    id: partial.id ?? "s1",
    name: partial.name ?? "Past chat",
    createdAt: partial.createdAt ?? "2026-01-01T00:00:00Z",
    updatedAt: partial.updatedAt ?? "2026-01-02T00:00:00Z",
  };
}

/**
 * Open the bubble and resume the given chat from the history list, leaving the
 * conversation view active. The history-list behaviour is the new entry point —
 * the bubble no longer auto-loads a single session on mount.
 */
async function openResumed(session: ChatSession, initialPath = "/") {
  chatService.listHistory.mockResolvedValue([summary({ id: session.id, name: session.name })]);
  chatService.getById.mockResolvedValue(session);
  const view = renderBubble(initialPath);
  fireEvent.click(await screen.findByLabelText("Open chat"));
  fireEvent.click(await screen.findByText(session.name ?? "New chat"));
  await screen.findByLabelText("Chat message");
  return view;
}

afterEach(() => {
  cleanup();
  for (const k of Object.keys(handlers)) delete handlers[k];
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

    renderBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));

    expect(await screen.findByText("Claude (claude-code)")).toBeTruthy();
    // `ild` is the only default-on entry; the toggle is labelled "ILD features".
    const ildBox = screen.getByLabelText("ILD features") as HTMLInputElement;
    expect(ildBox.checked).toBe(true);
    expect((screen.getByLabelText("Read") as HTMLInputElement).checked).toBe(false);
  });

  test("pre-selects the default provider in the dropdown", async () => {
    chatService.listHistory.mockResolvedValue([]);
    const other: AiProvider = { ...provider, id: "p0", name: "GPT", isDefault: false };
    // List the non-default first to prove selection follows isDefault, not order.
    aiProviderService.getAll.mockResolvedValue([other, provider]);

    renderBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));

    const select = (await screen.findByLabelText("AI provider")) as HTMLSelectElement;
    expect(select.value).toBe("p1");
  });

  test("leaves the dropdown unselected when no provider is the default", async () => {
    chatService.listHistory.mockResolvedValue([]);
    const a: AiProvider = { ...provider, id: "p0", name: "GPT", isDefault: false };
    const b: AiProvider = { ...provider, id: "p2", name: "Llama", isDefault: false };
    aiProviderService.getAll.mockResolvedValue([a, b]);

    renderBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));

    const select = (await screen.findByLabelText("AI provider")) as HTMLSelectElement;
    expect(select.value).toBe("");
  });

  test("sends the open work item id from the route as Chat Context", async () => {
    chatService.sendMessage.mockResolvedValue(undefined);
    await openResumed(chatSession(), "/taskboard/wi-77");

    const input = await screen.findByLabelText("Chat message");
    fireEvent.change(input, { target: { value: "look at this" } });
    fireEvent.click(screen.getByText("Send"));

    await waitFor(() =>
      expect(chatService.sendMessage).toHaveBeenCalledWith("s1", "look at this", "wi-77", null),
    );
  });

  test("sends the open Loop Editor's live document with the message", async () => {
    chatService.sendMessage.mockResolvedValue(undefined);
    const liveLoop = { $schema: "ild-loop-template/v1", name: "My Loop", nodes: [], edges: [] };
    getOpenLoopDocument.mockReturnValue(liveLoop);

    await openResumed(chatSession());

    const input = await screen.findByLabelText("Chat message");
    fireEvent.change(input, { target: { value: "edit the loop" } });
    fireEvent.click(screen.getByText("Send"));

    await waitFor(() =>
      expect(chatService.sendMessage).toHaveBeenCalledWith(
        "s1",
        "edit the loop",
        null,
        JSON.stringify(liveLoop),
      ),
    );
  });

  test("sends a null Chat Context when no work item is open", async () => {
    chatService.sendMessage.mockResolvedValue(undefined);
    await openResumed(chatSession(), "/taskboard");

    const input = await screen.findByLabelText("Chat message");
    fireEvent.change(input, { target: { value: "general question" } });
    fireEvent.click(screen.getByText("Send"));

    await waitFor(() =>
      expect(chatService.sendMessage).toHaveBeenCalledWith("s1", "general question", null, null),
    );
  });

  test("starting a chat locks in the session and reveals the input box", async () => {
    chatService.listHistory.mockResolvedValue([]);
    aiProviderService.getAll.mockResolvedValue([provider]);
    chatService.start.mockResolvedValue(chatSession({ name: null }));

    renderBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));
    fireEvent.change(await screen.findByLabelText("AI provider"), { target: { value: "p1" } });
    fireEvent.click(screen.getByText("Start chat"));

    const input = await screen.findByLabelText("Chat message");
    expect(input).toBeTruthy();
    expect(chatService.start).toHaveBeenCalledWith("p1", ["ild"]);
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

  test("lists past chats under Start chat with a name and date-stamp", async () => {
    chatService.listHistory.mockResolvedValue([
      summary({ id: "s1", name: "Deploy loop help", updatedAt: "2026-03-04T00:00:00Z" }),
      summary({ id: "s2", name: "Bug triage", updatedAt: "2026-03-03T00:00:00Z" }),
    ]);
    aiProviderService.getAll.mockResolvedValue([provider]);

    renderBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));

    expect(await screen.findByText("Deploy loop help")).toBeTruthy();
    expect(screen.getByText("Bug triage")).toBeTruthy();
    // The date-stamp renders the last-activity timestamp.
    expect(screen.getByText(new Date("2026-03-04T00:00:00Z").toLocaleString())).toBeTruthy();
  });

  test("per-chat delete removes that chat without touching the others", async () => {
    chatService.listHistory.mockResolvedValue([
      summary({ id: "s1", name: "Keep me" }),
      summary({ id: "s2", name: "Remove me" }),
    ]);
    chatService.deleteOne.mockResolvedValue(undefined);
    aiProviderService.getAll.mockResolvedValue([provider]);

    renderBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));
    await screen.findByText("Remove me");

    fireEvent.click(screen.getByLabelText("Delete chat Remove me"));

    await waitFor(() => expect(chatService.deleteOne).toHaveBeenCalledWith("s2"));
    await waitFor(() => expect(screen.queryByText("Remove me")).toBeNull());
    expect(screen.getByText("Keep me")).toBeTruthy();
  });

  test("delete all wipes every chat only after the confirmation", async () => {
    chatService.listHistory.mockResolvedValue([
      summary({ id: "s1", name: "One" }),
      summary({ id: "s2", name: "Two" }),
    ]);
    chatService.deleteAll.mockResolvedValue(undefined);
    aiProviderService.getAll.mockResolvedValue([provider]);

    renderBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));
    await screen.findByText("One");

    // First click only arms the confirmation — nothing is deleted yet.
    fireEvent.click(screen.getByText("Delete all"));
    expect(await screen.findByRole("alertdialog", { name: "Delete all chats?" })).toBeTruthy();
    expect(chatService.deleteAll).not.toHaveBeenCalled();

    // Confirming wipes the list.
    fireEvent.click(screen.getByRole("alertdialog").querySelector("button")!);
    await waitFor(() => expect(chatService.deleteAll).toHaveBeenCalled());
    await waitFor(() => expect(screen.queryByText("One")).toBeNull());
  });

  test("Back returns to the list and retains the chat (no delete)", async () => {
    await openResumed(chatSession({ name: "Retained chat" }));

    fireEvent.click(screen.getByText("← Back"));

    // The list is shown again and the chat was not deleted.
    expect(await screen.findByText("Start chat")).toBeTruthy();
    expect(screen.queryByLabelText("Chat message")).toBeNull();
    expect(chatService.deleteOne).not.toHaveBeenCalled();
    expect(chatService.deleteAll).not.toHaveBeenCalled();
  });

  test("reloads providers when ending a chat if the first load failed", async () => {
    chatService.listHistory.mockResolvedValue([summary({ id: "s1", name: "Old chat" })]);
    chatService.getById.mockResolvedValue(chatSession({ id: "s1", name: "Old chat" }));
    // The first provider load fails (e.g. a transient auth hiccup right at
    // startup); the retry when the start form re-appears succeeds.
    aiProviderService.getAll.mockRejectedValueOnce(new Error("boom")).mockResolvedValue([provider]);

    renderBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));

    // Resume the past chat (this does not need providers), then end it.
    fireEvent.click(await screen.findByText("Old chat"));
    await screen.findByLabelText("Chat message");
    fireEvent.click(screen.getByText("← Back"));

    // The provider list fills without having to close and reopen the panel.
    expect(await screen.findByText("Claude (claude-code)")).toBeTruthy();
  });

  test("publishes the chat session id so the loop editor can join the same group", async () => {
    await openResumed(chatSession());

    // The resumed session is published for other components (the LoopEditor).
    await waitFor(() => expect(setCurrentChatSessionId).toHaveBeenCalledWith("s1"));

    // Going back publishes null so the editor leaves the group.
    fireEvent.click(await screen.findByText("← Back"));
    await waitFor(() => expect(setCurrentChatSessionId).toHaveBeenCalledWith(null));
  });

  test("shows an in-progress indicator from send through streaming until completion", async () => {
    chatService.sendMessage.mockResolvedValue(undefined);
    await openResumed(chatSession());

    const input = await screen.findByLabelText("Chat message");
    fireEvent.change(input, { target: { value: "do the thing" } });
    fireEvent.click(screen.getByText("Send"));

    // Before any text streams back, the status reads "Thinking".
    const status = await screen.findByRole("status");
    expect(status.textContent).toContain("Thinking");

    // Once tokens stream in, the indicator must remain visible (the message is
    // still in progress) and switch to "Responding".
    act(() => {
      handlers.ChatTurnProgress?.({ payload: { chatSessionId: "s1", delta: "half an ans" } });
    });
    expect(await screen.findByText("half an ans")).toBeTruthy();
    expect(screen.getByRole("status").textContent).toContain("Responding");

    // The finalized reply ends the turn, so the indicator disappears.
    act(() => {
      handlers.ChatMessageAppended?.({
        payload: {
          chatSessionId: "s1",
          message: msg({ id: "a1", role: "assistant", content: "half an answer", sequence: 1 }),
        },
      });
    });
    await waitFor(() => expect(screen.queryByRole("status")).toBeNull());
    expect(screen.getByText("half an answer")).toBeTruthy();
  });

  test("the stop button only exists while a turn is in flight, and cancels it", async () => {
    chatService.sendMessage.mockResolvedValue(undefined);
    chatService.interrupt.mockResolvedValue(undefined);
    await openResumed(chatSession());

    // Idle: nothing to stop.
    expect(screen.queryByLabelText("Stop")).toBeNull();

    const input = await screen.findByLabelText("Chat message");
    fireEvent.change(input, { target: { value: "long task" } });
    fireEvent.click(screen.getByText("Send"));

    // In flight: the stop button appears alongside the (still present) Send.
    const stop = await screen.findByLabelText("Stop");
    expect(screen.getByText("Send")).toBeTruthy();

    fireEvent.click(stop);
    await waitFor(() => expect(chatService.interrupt).toHaveBeenCalledWith("s1"));

    // Busy is not cleared optimistically — the server ends the turn over the hub.
    expect(screen.getByLabelText("Stop")).toBeTruthy();
    act(() => {
      handlers.ChatTurnCompleted?.({ payload: { chatSessionId: "s1", interrupted: true } });
    });
    await waitFor(() => expect(screen.queryByLabelText("Stop")).toBeNull());
  });

  test("a stop that loses the race to the turn finishing is swallowed", async () => {
    chatService.sendMessage.mockResolvedValue(undefined);
    // The turn completed between render and click, so the chat no longer has one.
    chatService.interrupt.mockRejectedValue(new Error("Chat not found."));
    await openResumed(chatSession());

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
    chatService.sendMessage.mockResolvedValue(undefined);
    let release!: () => void;
    chatService.interrupt.mockReturnValue(
      new Promise<void>((resolve) => {
        release = resolve;
      }),
    );
    await openResumed(chatSession());

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
    await openResumed(chatSession());
    await screen.findByLabelText("Chat message");

    // Live streaming delta appears, then a finalized interrupted reply replaces it.
    act(() => {
      handlers.ChatTurnProgress?.({ payload: { chatSessionId: "s1", delta: "partial" } });
    });
    expect(await screen.findByText("partial")).toBeTruthy();

    act(() => {
      handlers.ChatMessageAppended?.({
        payload: {
          chatSessionId: "s1",
          message: msg({
            id: "a1",
            role: "assistant",
            content: "partial",
            interrupted: true,
            sequence: 1,
          }),
        },
      });
      handlers.ChatTurnCompleted?.({ payload: { chatSessionId: "s1", interrupted: true } });
    });

    await waitFor(() => expect(screen.getByText("interrupted")).toBeTruthy());
  });
});

describe("ChatBubble placement", () => {
  test("renders nothing when chat is disabled in settings", () => {
    localStorage.setItem(CHAT_ENABLED_KEY, "false");
    chatService.listHistory.mockResolvedValue([]);

    const { container } = renderBubble();
    expect(screen.queryByLabelText("Open chat")).toBeNull();
    expect(container.firstChild).toBeNull();
  });

  test("dragging the icon moves it, persists the spot, and suppresses the open click", async () => {
    chatService.listHistory.mockResolvedValue([]);
    aiProviderService.getAll.mockResolvedValue([provider]);

    renderBubble();
    const fab = await screen.findByLabelText("Open chat");

    fireEvent.pointerDown(fab, { clientX: 500, clientY: 500 });
    fireEvent.pointerMove(window, { clientX: 450, clientY: 450 });
    fireEvent.pointerUp(window, { clientX: 450, clientY: 450 });

    // jsdom is 1024×768, so the default corner is (952, 696); a -50/-50 drag
    // lands at (902, 646), still inside the viewport.
    expect((fab as HTMLElement).style.left).toBe("902px");
    expect((fab as HTMLElement).style.top).toBe("646px");
    expect(JSON.parse(localStorage.getItem(FAB_POSITION_KEY) ?? "{}")).toEqual({ x: 902, y: 646 });

    // The click that ends the drag must not open the panel.
    fireEvent.click(fab);
    expect(screen.queryByLabelText("AI provider")).toBeNull();
    expect(screen.getByLabelText("Open chat")).toBeTruthy();
  });

  test("resizing the panel updates and persists its size", async () => {
    chatService.listHistory.mockResolvedValue([]);
    aiProviderService.getAll.mockResolvedValue([provider]);

    renderBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));

    const panel = await screen.findByRole("dialog", { name: "AI chat" });
    const handle = screen.getByLabelText("Resize chat");

    fireEvent.pointerDown(handle, { clientX: 0, clientY: 0 });
    fireEvent.pointerMove(window, { clientX: 100, clientY: 80 });
    fireEvent.pointerUp(window, { clientX: 100, clientY: 80 });

    // Default size is 384×512; +100/+80 grows it to 484×592.
    expect((panel as HTMLElement).style.width).toBe("484px");
    expect((panel as HTMLElement).style.height).toBe("592px");
    expect(JSON.parse(localStorage.getItem(PANEL_SIZE_KEY) ?? "{}")).toEqual({
      width: 484,
      height: 592,
    });
  });

  test("a window resize clamps a now-off-screen icon back into view", async () => {
    localStorage.setItem(FAB_POSITION_KEY, JSON.stringify({ x: 900, y: 700 }));
    chatService.listHistory.mockResolvedValue([]);

    renderBubble();
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
    expect(JSON.parse(localStorage.getItem(FAB_POSITION_KEY) ?? "{}")).toEqual({ x: 328, y: 328 });
  });

  test("dragging the window header moves and persists the panel position", async () => {
    chatService.listHistory.mockResolvedValue([]);
    aiProviderService.getAll.mockResolvedValue([provider]);

    renderBubble();
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
    expect(JSON.parse(localStorage.getItem(PANEL_POSITION_KEY) ?? "{}")).toEqual({
      x: 540,
      y: 176,
    });
  });

  test("a header button press does not drag the window", async () => {
    await openResumed(chatSession());

    const panel = await screen.findByRole("dialog", { name: "AI chat" });
    const back = await screen.findByText("← Back");

    // Pressing and moving on a header button must not reposition the panel…
    fireEvent.pointerDown(back, { clientX: 200, clientY: 200 });
    fireEvent.pointerMove(window, { clientX: 50, clientY: 50 });
    fireEvent.pointerUp(window, { clientX: 50, clientY: 50 });

    expect((panel as HTMLElement).style.left).toBe("620px");
    expect((panel as HTMLElement).style.top).toBe("236px");
    expect(localStorage.getItem(PANEL_POSITION_KEY)).toBeNull();

    // …and the button still does its job, returning to the list.
    fireEvent.click(back);
    expect(await screen.findByText("Start chat")).toBeTruthy();
  });
});
