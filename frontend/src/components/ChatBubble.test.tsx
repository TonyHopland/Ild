import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor, act } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { AiProvider, ChatMessage, ChatSession } from "../types";
import { FAB_POSITION_KEY, PANEL_POSITION_KEY, PANEL_SIZE_KEY } from "./chatPlacement";
import { CHAT_ENABLED_KEY } from "../hooks/useChatEnabled";

// Hoisted so the vi.mock factories (which are hoisted to the top) can reference
// them without a temporal-dead-zone error.
const { handlers, chatService, aiProviderService, getOpenLoopDocument, setCurrentChatSessionId } =
  vi.hoisted(() => ({
    handlers: {} as Record<string, (msg: { payload: unknown }) => void>,
    chatService: {
      get: vi.fn(),
      start: vi.fn(),
      sendMessage: vi.fn(),
      end: vi.fn(),
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
    chatService.get.mockResolvedValue(null);
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
    chatService.get.mockResolvedValue(null);
    const other: AiProvider = { ...provider, id: "p0", name: "GPT", isDefault: false };
    // List the non-default first to prove selection follows isDefault, not order.
    aiProviderService.getAll.mockResolvedValue([other, provider]);

    renderBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));

    const select = (await screen.findByLabelText("AI provider")) as HTMLSelectElement;
    expect(select.value).toBe("p1");
  });

  test("leaves the dropdown unselected when no provider is the default", async () => {
    chatService.get.mockResolvedValue(null);
    const a: AiProvider = { ...provider, id: "p0", name: "GPT", isDefault: false };
    const b: AiProvider = { ...provider, id: "p2", name: "Llama", isDefault: false };
    aiProviderService.getAll.mockResolvedValue([a, b]);

    renderBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));

    const select = (await screen.findByLabelText("AI provider")) as HTMLSelectElement;
    expect(select.value).toBe("");
  });

  test("sends the open work item id from the route as Chat Context", async () => {
    const session: ChatSession = {
      id: "s1",
      aiProviderId: "p1",
      providerType: "claude-code",
      tools: ["ild"],
      createdAt: "2026-01-01T00:00:00Z",
      messages: [],
    };
    chatService.get.mockResolvedValue(session);
    chatService.sendMessage.mockResolvedValue(undefined);

    renderBubble("/taskboard/wi-77");
    fireEvent.click(await screen.findByLabelText("Open chat"));

    const input = await screen.findByLabelText("Chat message");
    fireEvent.change(input, { target: { value: "look at this" } });
    fireEvent.click(screen.getByText("Send"));

    await waitFor(() =>
      expect(chatService.sendMessage).toHaveBeenCalledWith("look at this", "wi-77", null),
    );
  });

  test("sends the open Loop Editor's live document with the message", async () => {
    const session: ChatSession = {
      id: "s1",
      aiProviderId: "p1",
      providerType: "claude-code",
      tools: ["ild"],
      createdAt: "2026-01-01T00:00:00Z",
      messages: [],
    };
    chatService.get.mockResolvedValue(session);
    chatService.sendMessage.mockResolvedValue(undefined);
    const liveLoop = { $schema: "ild-loop-template/v1", name: "My Loop", nodes: [], edges: [] };
    getOpenLoopDocument.mockReturnValue(liveLoop);

    renderBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));

    const input = await screen.findByLabelText("Chat message");
    fireEvent.change(input, { target: { value: "edit the loop" } });
    fireEvent.click(screen.getByText("Send"));

    await waitFor(() =>
      expect(chatService.sendMessage).toHaveBeenCalledWith(
        "edit the loop",
        null,
        JSON.stringify(liveLoop),
      ),
    );
  });

  test("sends a null Chat Context when no work item is open", async () => {
    const session: ChatSession = {
      id: "s1",
      aiProviderId: "p1",
      providerType: "claude-code",
      tools: ["ild"],
      createdAt: "2026-01-01T00:00:00Z",
      messages: [],
    };
    chatService.get.mockResolvedValue(session);
    chatService.sendMessage.mockResolvedValue(undefined);

    renderBubble("/taskboard");
    fireEvent.click(await screen.findByLabelText("Open chat"));

    const input = await screen.findByLabelText("Chat message");
    fireEvent.change(input, { target: { value: "general question" } });
    fireEvent.click(screen.getByText("Send"));

    await waitFor(() =>
      expect(chatService.sendMessage).toHaveBeenCalledWith("general question", null, null),
    );
  });

  test("starting a chat locks in the session and reveals the input box", async () => {
    chatService.get.mockResolvedValue(null);
    aiProviderService.getAll.mockResolvedValue([provider]);
    const session: ChatSession = {
      id: "s1",
      aiProviderId: "p1",
      providerType: "claude-code",
      tools: ["ild"],
      createdAt: "2026-01-01T00:00:00Z",
      messages: [],
    };
    chatService.start.mockResolvedValue(session);

    renderBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));
    fireEvent.change(await screen.findByLabelText("AI provider"), { target: { value: "p1" } });
    fireEvent.click(screen.getByText("Start chat"));

    const input = await screen.findByLabelText("Chat message");
    expect(input).toBeTruthy();
    expect(chatService.start).toHaveBeenCalledWith("p1", ["ild"]);
  });

  test("rehydrates an existing transcript on mount", async () => {
    const session: ChatSession = {
      id: "s1",
      aiProviderId: "p1",
      providerType: "claude-code",
      tools: ["ild"],
      createdAt: "2026-01-01T00:00:00Z",
      messages: [
        msg({ id: "m1", role: "user", content: "hello", sequence: 0 }),
        msg({ id: "m2", role: "assistant", content: "hi back", sequence: 1 }),
      ],
    };
    chatService.get.mockResolvedValue(session);

    renderBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));

    expect(await screen.findByText("hello")).toBeTruthy();
    expect(screen.getByText("hi back")).toBeTruthy();
  });

  test("publishes the chat session id so the loop editor can join the same group", async () => {
    const session: ChatSession = {
      id: "s1",
      aiProviderId: "p1",
      providerType: "claude-code",
      tools: ["ild"],
      createdAt: "2026-01-01T00:00:00Z",
      messages: [],
    };
    chatService.get.mockResolvedValue(session);
    chatService.end.mockResolvedValue(undefined);

    renderBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));

    // The loaded session is published for other components (the LoopEditor).
    await waitFor(() => expect(setCurrentChatSessionId).toHaveBeenCalledWith("s1"));

    // Ending the session publishes null so the editor leaves the group.
    fireEvent.click(await screen.findByText("End chat"));
    await waitFor(() => expect(setCurrentChatSessionId).toHaveBeenCalledWith(null));
  });

  test("streams a turn and flags an interrupted partial reply", async () => {
    const session: ChatSession = {
      id: "s1",
      aiProviderId: "p1",
      providerType: "claude-code",
      tools: ["ild"],
      createdAt: "2026-01-01T00:00:00Z",
      messages: [],
    };
    chatService.get.mockResolvedValue(session);

    renderBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));
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
    chatService.get.mockResolvedValue(null);

    const { container } = renderBubble();
    expect(screen.queryByLabelText("Open chat")).toBeNull();
    expect(container.firstChild).toBeNull();
  });

  test("dragging the icon moves it, persists the spot, and suppresses the open click", async () => {
    chatService.get.mockResolvedValue(null);
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
    chatService.get.mockResolvedValue(null);
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
    chatService.get.mockResolvedValue(null);

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
    chatService.get.mockResolvedValue(null);
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
    const session: ChatSession = {
      id: "s1",
      aiProviderId: "p1",
      providerType: "claude-code",
      tools: ["ild"],
      createdAt: "2026-01-01T00:00:00Z",
      messages: [],
    };
    chatService.get.mockResolvedValue(session);

    renderBubble();
    fireEvent.click(await screen.findByLabelText("Open chat"));

    const panel = await screen.findByRole("dialog", { name: "AI chat" });
    const endChat = await screen.findByText("End chat");

    // Pressing and moving on a header button must not reposition the panel…
    fireEvent.pointerDown(endChat, { clientX: 200, clientY: 200 });
    fireEvent.pointerMove(window, { clientX: 50, clientY: 50 });
    fireEvent.pointerUp(window, { clientX: 50, clientY: 50 });

    expect((panel as HTMLElement).style.left).toBe("620px");
    expect((panel as HTMLElement).style.top).toBe("236px");
    expect(localStorage.getItem(PANEL_POSITION_KEY)).toBeNull();

    // …and the button still does its job.
    fireEvent.click(endChat);
    expect(chatService.end).toHaveBeenCalled();
  });
});
