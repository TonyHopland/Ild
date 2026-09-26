import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import type { ChatSession, WorkItemEditProposal } from "../types";

const { handlers, invoke, chatService, aiProviderService, workItemService } = vi.hoisted(() => ({
  handlers: {} as Record<string, (msg: { payload: unknown }) => void>,
  invoke: vi.fn(() => Promise.resolve()),
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
  workItemService: {
    listEditProposals: vi.fn(),
    listEditProposalsFor: vi.fn(),
    approveEditProposal: vi.fn(),
    rejectEditProposal: vi.fn(),
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
    invoke,
  }),
}));
vi.mock("../services/auth", () => ({ chatService, aiProviderService, workItemService }));
vi.mock("../utils/openLoopDocument", () => ({ getOpenLoopDocument: vi.fn() }));
vi.mock("../services/chatSessionStore", () => ({ setCurrentChatSessionId: vi.fn() }));

import ChatBubble from "./ChatBubble";

function session(): ChatSession {
  return {
    id: "s1",
    name: "Tidy the backlog",
    aiProviderId: "p1",
    providerType: "claude-code",
    tools: ["ild"],
    createdAt: "2026-01-01T00:00:00Z",
    messages: [
      {
        id: "m1",
        role: "assistant",
        content: "I proposed a sharper title for wi-9.",
        interrupted: false,
        sequence: 1,
        createdAt: "2026-01-01T00:00:00Z",
      },
    ],
    activeTurnId: null,
  };
}

function proposal(overrides: Partial<WorkItemEditProposal> = {}): WorkItemEditProposal {
  return {
    id: "p-1",
    workItemId: "wi-9",
    status: "Pending",
    proposed: { title: "Agent's sharper title" },
    snapshot: {
      title: "Old title",
      description: null,
      tags: [],
      branchNameOverride: null,
      baseBranchOverride: null,
    },
    rationale: null,
    rejectionReason: null,
    createdByLoopRunId: null,
    createdByChatSessionId: "s1",
    createdAt: "2026-09-26T10:00:00Z",
    decidedAt: null,
    ...overrides,
  };
}

async function openResumed() {
  chatService.listHistory.mockResolvedValue([
    {
      id: "s1",
      name: "Tidy the backlog",
      createdAt: "2026-01-01T00:00:00Z",
      updatedAt: "2026-01-02T00:00:00Z",
    },
  ]);
  chatService.getById.mockResolvedValue(session());
  aiProviderService.getAll.mockResolvedValue([]);
  render(
    <MemoryRouter initialEntries={["/"]}>
      <ChatBubble />
    </MemoryRouter>,
  );
  fireEvent.click(await screen.findByLabelText("Open chat"));
  fireEvent.click(await screen.findByText("Tidy the backlog"));
  await screen.findByLabelText("Chat message");
}

afterEach(() => {
  cleanup();
  for (const k of Object.keys(handlers)) delete handlers[k];
  vi.clearAllMocks();
  localStorage.clear();
});

describe("ChatBubble edit proposals", () => {
  test("the chat that made a proposal shows it in its transcript with a working Approve", async () => {
    workItemService.listEditProposalsFor.mockResolvedValue([proposal()]);
    workItemService.approveEditProposal.mockResolvedValue({
      proposal: proposal({ status: "Approved" }),
      workItem: {},
    });

    await openResumed();

    const transcript = (await screen.findByText("I proposed a sharper title for wi-9.")).closest(
      ".chat-panel-body",
    ) as HTMLElement;
    await waitFor(() => expect(transcript.textContent).toContain("Agent's sharper title"));
    expect(transcript.textContent).toContain("Old title");
    expect(workItemService.listEditProposalsFor).toHaveBeenCalledWith(
      expect.objectContaining({ chatSessionId: "s1" }),
    );
    expect(invoke).toHaveBeenCalledWith("SubscribeToChat", "s1");

    fireEvent.click(within(transcript).getByRole("button", { name: "Approve" }));

    await waitFor(() =>
      expect(workItemService.approveEditProposal).toHaveBeenCalledWith("wi-9", "p-1"),
    );
  });
});
