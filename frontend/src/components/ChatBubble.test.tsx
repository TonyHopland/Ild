import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor, act } from "@testing-library/react";
import type { AiProvider, ChatMessage, ChatSession } from "../types";

// Hoisted so the vi.mock factories (which are hoisted to the top) can reference
// them without a temporal-dead-zone error.
const { handlers, chatService, aiProviderService } = vi.hoisted(() => ({
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

import ChatBubble from "./ChatBubble";

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
});

describe("ChatBubble", () => {
  test("opening with no session prompts for a provider with ILD tools pre-checked", async () => {
    chatService.get.mockResolvedValue(null);
    aiProviderService.getAll.mockResolvedValue([provider]);

    render(<ChatBubble />);
    fireEvent.click(await screen.findByLabelText("Open chat"));

    expect(await screen.findByText("Claude (claude-code)")).toBeTruthy();
    // `ild` is the only default-on entry.
    const ildBox = screen.getByLabelText("ILD work items") as HTMLInputElement;
    expect(ildBox.checked).toBe(true);
    expect((screen.getByLabelText("Read") as HTMLInputElement).checked).toBe(false);
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

    render(<ChatBubble />);
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

    render(<ChatBubble />);
    fireEvent.click(await screen.findByLabelText("Open chat"));

    expect(await screen.findByText("hello")).toBeTruthy();
    expect(screen.getByText("hi back")).toBeTruthy();
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

    render(<ChatBubble />);
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
