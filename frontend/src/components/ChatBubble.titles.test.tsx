import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor, act, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import type { ChatMessage, ChatSession, ChatSessionSummary } from "../types";

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
    rename: vi.fn(),
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
import { openChatFromList, openChatList, startNewChat } from "../test-support";

// A stand-in for the server: what GET /chat/history answers right now. A rename
// that succeeds changes it there, as PUT /chat/{id}/name does.
let server: ChatSessionSummary[] = [];
const sessions: Record<string, ChatSession> = {};

function summary(id: string, name: string): ChatSessionSummary {
  return {
    id,
    name,
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-02T00:00:00Z",
    hasUnread: false,
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

function chat(id: string, name: string): ChatSession {
  return {
    id,
    name,
    aiProviderId: "p1",
    providerType: "claude-code",
    tools: ["ild"],
    createdAt: "2026-01-01T00:00:00Z",
    messages: [msg(id, 0, "user"), msg(id, 1, "assistant")],
    activeTurnId: null,
  };
}

function setName(id: string, name: string) {
  server = server.map((c) => (c.id === id ? { ...c, name } : c));
}

beforeEach(() => {
  chatService.listHistory.mockImplementation(() => Promise.resolve(server.map((c) => ({ ...c }))));
  chatService.getById.mockImplementation((id: string) => Promise.resolve(sessions[id]));
  chatService.markRead.mockResolvedValue(undefined);
  chatService.rename.mockImplementation((id: string, name: string) => {
    setName(id, name);
    return Promise.resolve();
  });
});

afterEach(() => {
  cleanup();
  for (const k of Object.keys(handlers)) delete handlers[k];
  for (const k of Object.keys(sessions)) delete sessions[k];
  connection.state = "connected";
  server = [];
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

async function settle() {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
}

function emitTitleChanged(chatSessionId: string) {
  act(() => {
    handlers.ChatTitleChanged?.({ payload: { chatSessionId } });
  });
}

function panel() {
  return screen.getByRole("dialog");
}

async function openPanel() {
  fireEvent.click(await screen.findByRole("button", { name: "Open chat" }));
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

/** The rename draft's input, if one is open: any text box but the message box. */
function renameInput(): HTMLInputElement | undefined {
  const message = screen.queryByLabelText("Chat message");
  return within(panel())
    .queryAllByRole("textbox")
    .find((box) => box !== message) as HTMLInputElement | undefined;
}

function startRenamingRow(name: string): HTMLInputElement {
  fireEvent.click(screen.getByRole("button", { name: `Rename chat ${name}` }));
  return renameInput()!;
}

function startRenamingHeader(): HTMLInputElement {
  fireEvent.click(within(panel()).getByRole("button", { name: /^rename/i }));
  return renameInput()!;
}

/** Enter as a browser delivers it: a form submits, a lone input sees the key. */
function pressEnter(input: HTMLInputElement) {
  if (input.form) fireEvent.submit(input.form);
  else fireEvent.keyDown(input, { key: "Enter", code: "Enter" });
}

describe("chat titles", () => {
  test("a title saved on the server replaces the open chat's header and the list row without a reload", async () => {
    server = [summary("a", "Could this be solved better by")];
    sessions.a = chat("a", "Could this be solved better by");
    render(bubble());
    await openPanel();
    await resume("Could this be solved better by");

    setName("a", "Login page fix");
    emitTitleChanged("a");
    await within(panel()).findByText("Login page fix");
    expect(within(panel()).queryByText("Could this be solved better by")).toBeNull();

    await back();
    expect(screen.getByText("Login page fix")).toBeTruthy();

    setName("a", "Login page redirect loop");
    emitTitleChanged("a");
    await screen.findByText("Login page redirect loop");
    expect(screen.queryByText("Login page fix")).toBeNull();
  });

  test("a row is renamed inline: Enter saves, Cancel and Escape discard, and an empty name cannot be saved", async () => {
    server = [summary("a", "Alpha"), summary("b", "Beta")];
    render(bubble());
    await openPanel();
    await openChatList();
    await screen.findByText("Alpha");

    let input = startRenamingRow("Alpha");
    fireEvent.change(input, { target: { value: "Deploy loop wiring" } });
    pressEnter(input);
    await waitFor(() => expect(chatService.rename).toHaveBeenCalledWith("a", "Deploy loop wiring"));
    await screen.findByText("Deploy loop wiring");
    await waitFor(() => expect(renameInput()).toBeUndefined());

    input = startRenamingRow("Deploy loop wiring");
    fireEvent.change(input, { target: { value: "Thrown away" } });
    fireEvent.click(within(panel()).getByRole("button", { name: "Cancel" }));
    expect(renameInput()).toBeUndefined();

    input = startRenamingRow("Beta");
    fireEvent.change(input, { target: { value: "Thrown away too" } });
    fireEvent.keyDown(input, { key: "Escape", code: "Escape" });
    expect(renameInput()).toBeUndefined();

    input = startRenamingRow("Beta");
    fireEvent.change(input, { target: { value: "   " } });
    expect(
      (within(panel()).getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled,
    ).toBe(true);
    pressEnter(input);
    await settle();

    expect(chatService.rename).toHaveBeenCalledTimes(1);
    expect(screen.getByText("Deploy loop wiring")).toBeTruthy();
    expect(screen.queryByText("Thrown away")).toBeNull();
    expect(screen.queryByText("Thrown away too")).toBeNull();
  });

  test("the open chat is renamed from its header", async () => {
    server = [summary("a", "Alpha")];
    sessions.a = chat("a", "Alpha");
    render(bubble());
    await openPanel();
    await resume("Alpha");

    const input = startRenamingHeader();
    fireEvent.change(input, { target: { value: "Login page fix" } });
    fireEvent.click(within(panel()).getByRole("button", { name: "Save" }));

    await waitFor(() => expect(chatService.rename).toHaveBeenCalledWith("a", "Login page fix"));
    await within(panel()).findByText("Login page fix");
    expect(renameInput()).toBeUndefined();
    // The chat itself carries on.
    expect(screen.getByLabelText("Chat message")).toBeTruthy();
  });

  test("a failed rename shows an error and keeps the old title", async () => {
    server = [summary("a", "Alpha")];
    chatService.rename.mockRejectedValue(new Error("Name is taken"));
    render(bubble());
    await openPanel();
    await openChatList();
    await screen.findByText("Alpha");

    const input = startRenamingRow("Alpha");
    fireEvent.change(input, { target: { value: "Deploy loop wiring" } });
    pressEnter(input);

    await within(panel()).findByText("Name is taken");
    const draft = renameInput();
    if (draft) fireEvent.keyDown(draft, { key: "Escape", code: "Escape" });
    expect(screen.getByText("Alpha")).toBeTruthy();
    expect(screen.queryByText("Deploy loop wiring")).toBeNull();
  });

  test("leaving or switching chat discards an unsaved rename", async () => {
    server = [summary("a", "Alpha"), summary("b", "Beta")];
    sessions.a = chat("a", "Alpha");
    sessions.b = chat("b", "Beta");
    render(bubble());
    await openPanel();
    await resume("Alpha");

    fireEvent.change(startRenamingHeader(), { target: { value: "Unsaved header name" } });
    await back();
    expect(renameInput()).toBeUndefined();
    expect(screen.getByText("Alpha")).toBeTruthy();

    fireEvent.change(startRenamingRow("Alpha"), { target: { value: "Unsaved row name" } });
    await resume("Beta");
    expect(renameInput()).toBeUndefined();
    await back();
    expect(renameInput()).toBeUndefined();

    await resume("Alpha");
    expect(renameInput()).toBeUndefined();
    expect(within(panel()).getByText("Alpha")).toBeTruthy();
    expect(chatService.rename).not.toHaveBeenCalled();
  });
});
