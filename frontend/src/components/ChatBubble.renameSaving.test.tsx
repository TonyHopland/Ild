import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor, act, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import type { ChatSession, ChatSessionSummary } from "../types";

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

function panel() {
  return screen.getByRole("dialog");
}

async function openPanel() {
  fireEvent.click(await screen.findByRole("button", { name: "Open chat" }));
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

/** Enter as a browser delivers it: a form submits, a lone input sees the key. */
function pressEnter(input: HTMLInputElement) {
  if (input.form) fireEvent.submit(input.form);
  else fireEvent.keyDown(input, { key: "Enter", code: "Enter" });
}

describe("a rename being saved", () => {
  test("cannot be edited while its save is out, so what is saved is what was shown", async () => {
    server = [summary("a", "Alpha")];
    let saved!: () => void;
    chatService.rename.mockImplementation(
      (id: string, name: string) =>
        new Promise<void>((resolve) => {
          saved = () => {
            setName(id, name);
            resolve();
          };
        }),
    );
    render(bubble());
    await openPanel();
    await screen.findByText("Alpha");

    const input = startRenamingRow("Alpha");
    fireEvent.change(input, { target: { value: "Deploy loop wiring" } });
    pressEnter(input);
    await waitFor(() => expect(chatService.rename).toHaveBeenCalledWith("a", "Deploy loop wiring"));

    expect(renameInput()!.readOnly).toBe(true);

    await act(async () => saved());
    await screen.findByText("Deploy loop wiring");
    await waitFor(() => expect(renameInput()).toBeUndefined());
  });

  test("can be edited again once a save fails", async () => {
    server = [summary("a", "Alpha")];
    chatService.rename.mockRejectedValue(new Error("Name is taken"));
    render(bubble());
    await openPanel();
    await screen.findByText("Alpha");

    const input = startRenamingRow("Alpha");
    fireEvent.change(input, { target: { value: "Deploy loop wiring" } });
    pressEnter(input);

    await within(panel()).findByText("Name is taken");
    expect(renameInput()!.readOnly).toBe(false);
  });
});
