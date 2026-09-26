import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor, within, act } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import type { ChatMessage, ChatSession, WorkItemEditProposal } from "../types";

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

function message(sequence: number, role: "user" | "assistant", content: string): ChatMessage {
  return {
    id: `m${sequence}`,
    role,
    content,
    interrupted: false,
    sequence,
    createdAt: "2026-01-01T00:00:00Z",
  };
}

/** A chat-made card proposing `title`, anchored after the reply with sequence `anchor`. */
function card(
  id: string,
  title: string,
  anchor: number | null,
  createdAt: string,
  overrides: Partial<WorkItemEditProposal> = {},
): WorkItemEditProposal {
  return proposal({ id, proposed: { title }, chatReplySequence: anchor, createdAt, ...overrides });
}

/** The labels, in the order their elements appear in the document. */
function documentOrder(entries: [string, Element][]): string[] {
  return [...entries]
    .sort(([, a], [, b]) =>
      a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1,
    )
    .map(([label]) => label);
}

function byText(labels: string[]): [string, Element][] {
  return labels.map((l) => [l, screen.getByText(l)]);
}

async function openResumed(view: ChatSession = session()) {
  chatService.listHistory.mockResolvedValue([
    {
      id: "s1",
      name: "Tidy the backlog",
      createdAt: "2026-01-01T00:00:00Z",
      updatedAt: "2026-01-02T00:00:00Z",
    },
  ]);
  chatService.getById.mockResolvedValue(view);
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

  test("each card sits after the reply of the turn that made it, and a decision does not move it", async () => {
    const pendingA = card("p-a", "Title A", 1, "2026-09-26T10:00:00Z");
    const listed = [
      card("p-n2", "Title N2", null, "2026-09-26T11:00:00Z"),
      pendingA,
      card("p-e", "Title E", 7, "2026-09-26T10:30:00Z"),
      card("p-c", "Title C", 3, "2026-09-26T10:20:00Z", {
        status: "Approved",
        decidedAt: "2026-09-26T10:25:00Z",
      }),
      card("p-b", "Title B", 1, "2026-09-26T10:00:00Z"),
      card("p-z", "Title Z", 1, "2026-09-26T09:00:00Z"),
      card("p-n1", "Title N1", null, "2026-09-26T08:00:00Z"),
    ];
    workItemService.listEditProposalsFor.mockResolvedValueOnce(listed);
    workItemService.listEditProposalsFor.mockResolvedValue(
      listed.map((p) =>
        p === pendingA ? { ...p, status: "Approved", decidedAt: "2026-09-26T12:00:00Z" } : p,
      ),
    );
    workItemService.approveEditProposal.mockResolvedValue({
      proposal: { ...pendingA, status: "Approved" },
      workItem: {},
    });

    await openResumed({
      ...session(),
      messages: [
        message(0, "user", "Tidy wi-9 please"),
        message(1, "assistant", "Proposed three titles."),
        message(2, "user", "And one more?"),
        message(3, "assistant", "Proposed another."),
        message(4, "user", "Thanks"),
        message(5, "assistant", "Anything else?"),
      ],
    });
    await screen.findByText("Title A");

    const expected = [
      "Tidy wi-9 please",
      "Proposed three titles.",
      "Title Z",
      "Title A",
      "Title B",
      "And one more?",
      "Proposed another.",
      "Title C",
      "Thanks",
      "Anything else?",
      "Title E",
      "Title N1",
      "Title N2",
    ];
    expect(documentOrder(byText(expected))).toEqual(expected);

    const cardA = screen.getByText("Title A").closest(".edit-proposal-card") as HTMLElement;
    fireEvent.click(within(cardA).getByRole("button", { name: "Approve" }));

    await waitFor(() =>
      expect(
        within(
          screen.getByText("Title A").closest(".edit-proposal-card") as HTMLElement,
        ).queryByRole("button", { name: "Approve" }),
      ).toBeNull(),
    );
    expect(documentOrder(byText(expected))).toEqual(expected);
  });

  test("a card made by the turn in flight follows its reply as it streams and once it lands", async () => {
    workItemService.listEditProposalsFor.mockResolvedValue([
      card("p-old", "Earlier title", 1, "2026-09-26T09:00:00Z"),
      card("p-now", "Sharper title", 3, "2026-09-26T10:00:00Z"),
    ]);

    await openResumed({
      ...session(),
      messages: [
        message(0, "user", "Rename wi-9"),
        message(1, "assistant", "To what?"),
        message(2, "user", "Something sharper"),
      ],
      activeTurnId: "t1",
    });
    await screen.findByText("Sharper title");
    const indicator = () => ["indicator", screen.getByRole("status")] as [string, Element];

    expect(
      documentOrder([
        ...byText(["To what?", "Earlier title", "Something sharper", "Sharper title"]),
        indicator(),
      ]),
    ).toEqual(["To what?", "Earlier title", "Something sharper", "Sharper title", "indicator"]);

    act(() =>
      handlers["ChatTurnProgress"]({
        payload: { chatSessionId: "s1", turnId: "t1", delta: "Renaming it now" },
      }),
    );

    expect(
      documentOrder([
        ...byText(["Something sharper", "Renaming it now", "Sharper title"]),
        indicator(),
      ]),
    ).toEqual(["Something sharper", "Renaming it now", "Sharper title", "indicator"]);

    act(() => {
      handlers["ChatMessageAppended"]({
        payload: {
          chatSessionId: "s1",
          turnId: "t1",
          message: message(3, "assistant", "Renamed it."),
        },
      });
      handlers["ChatTurnCompleted"]({
        payload: { chatSessionId: "s1", turnId: "t1", interrupted: false },
      });
    });

    expect(screen.queryByRole("status")).toBeNull();
    expect(documentOrder(byText(["Something sharper", "Renamed it.", "Sharper title"]))).toEqual([
      "Something sharper",
      "Renamed it.",
      "Sharper title",
    ]);

    act(() =>
      handlers["ChatMessageAppended"]({
        payload: { chatSessionId: "s1", turnId: "t2", message: message(4, "user", "Now the tags") },
      }),
    );

    expect(documentOrder(byText(["Renamed it.", "Sharper title", "Now the tags"]))).toEqual([
      "Renamed it.",
      "Sharper title",
      "Now the tags",
    ]);
  });

  test("a card keeps a reason being typed when its reply lands and it moves under it", async () => {
    workItemService.listEditProposalsFor.mockResolvedValue([
      card("p-now", "Sharper title", 1, "2026-09-26T10:00:00Z"),
    ]);
    await openResumed({
      ...session(),
      messages: [message(0, "user", "Rename wi-9")],
      activeTurnId: "t1",
    });
    fireEvent.click(await screen.findByRole("button", { name: "Reject" }));
    fireEvent.change(screen.getByLabelText("Rejection reason (optional)"), {
      target: { value: "Too long" },
    });

    act(() => {
      handlers["ChatMessageAppended"]({
        payload: { chatSessionId: "s1", turnId: "t1", message: message(1, "assistant", "Done.") },
      });
      handlers["ChatMessageAppended"]({
        payload: { chatSessionId: "s1", turnId: "t2", message: message(2, "user", "Next") },
      });
    });

    expect(documentOrder(byText(["Done.", "Sharper title", "Next"]))).toEqual([
      "Done.",
      "Sharper title",
      "Next",
    ]);
    expect(
      (screen.getByLabelText("Rejection reason (optional)") as HTMLTextAreaElement).value,
    ).toBe("Too long");
  });
});
