import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { screen, fireEvent, waitFor, within, act } from "@testing-library/react";
import type { ChatMessage, ChatSession, WorkItem, WorkItemEditProposal } from "../types";
import { documentOrder, emitHubEvent, proposalCardAround, signalRHub } from "../test-support";
import {
  chatMessage,
  chatSession,
  openResumedChat as openResumed,
  stubChatServices,
} from "../test-support.chatBubble";

vi.mock("../hooks/useSignalR", async () => (await import("../test-support")).fakeSignalRModule());
vi.mock("../utils/openLoopDocument", () => ({ getOpenLoopDocument: vi.fn() }));
vi.mock("../services/chatSessionStore", () => ({ setCurrentChatSessionId: vi.fn() }));

const services = stubChatServices();
const { workItemService } = services;

function session(): ChatSession {
  return chatSession({
    id: "s1",
    name: "Tidy the backlog",
    messages: [
      chatMessage({
        id: "m1",
        content: "I proposed a sharper title for wi-9.",
        sequence: 1,
      }),
    ],
  });
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

afterEach(() => {
  signalRHub.reset();
  services.reset();
  vi.clearAllMocks();
  localStorage.clear();
});

describe("ChatBubble edit proposals", () => {
  test("the chat that made a proposal shows it in its transcript with a working Approve", async () => {
    workItemService.listEditProposalsFor.mockResolvedValue([proposal()]);
    workItemService.approveEditProposal.mockResolvedValue({
      proposal: proposal({ status: "Approved" }),
      workItem: {} as WorkItem,
    });

    await openResumed(session());

    const transcript = (await screen.findByText("I proposed a sharper title for wi-9.")).closest(
      ".chat-panel-body",
    ) as HTMLElement;
    await waitFor(() => expect(transcript.textContent).toContain("Agent's sharper title"));
    expect(transcript.textContent).toContain("Old title");
    expect(workItemService.listEditProposalsFor).toHaveBeenCalledWith(
      expect.objectContaining({ chatSessionId: "s1" }),
    );
    expect(signalRHub.invoke).toHaveBeenCalledWith("SubscribeToChat", "s1");

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
      workItem: {} as WorkItem,
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
    expect(documentOrder(expected)).toEqual(expected);

    const cardA = proposalCardAround(screen.getByText("Title A"));
    fireEvent.click(within(cardA).getByRole("button", { name: "Approve" }));

    await waitFor(() =>
      expect(
        within(proposalCardAround(screen.getByText("Title A"))).queryByRole("button", {
          name: "Approve",
        }),
      ).toBeNull(),
    );
    expect(documentOrder(expected)).toEqual(expected);
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

    expect(
      documentOrder([
        "To what?",
        "Earlier title",
        "Something sharper",
        "Sharper title",
        "Thinking",
      ]),
    ).toEqual(["To what?", "Earlier title", "Something sharper", "Sharper title", "Thinking"]);

    emitHubEvent("ChatTurnProgress", {
      chatSessionId: "s1",
      turnId: "t1",
      delta: "Renaming it now",
    });

    expect(
      documentOrder(["Something sharper", "Renaming it now", "Sharper title", "Responding"]),
    ).toEqual(["Something sharper", "Renaming it now", "Sharper title", "Responding"]);

    act(() => {
      signalRHub.dispatch("ChatMessageAppended", {
        chatSessionId: "s1",
        turnId: "t1",
        message: message(3, "assistant", "Renamed it."),
      });
      signalRHub.dispatch("ChatTurnCompleted", {
        chatSessionId: "s1",
        turnId: "t1",
        interrupted: false,
      });
    });

    expect(screen.queryByRole("status")).toBeNull();
    expect(documentOrder(["Something sharper", "Renamed it.", "Sharper title"])).toEqual([
      "Something sharper",
      "Renamed it.",
      "Sharper title",
    ]);

    emitHubEvent("ChatMessageAppended", {
      chatSessionId: "s1",
      turnId: "t2",
      message: message(4, "user", "Now the tags"),
    });

    expect(documentOrder(["Renamed it.", "Sharper title", "Now the tags"])).toEqual([
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
      signalRHub.dispatch("ChatMessageAppended", {
        chatSessionId: "s1",
        turnId: "t1",
        message: message(1, "assistant", "Done."),
      });
      signalRHub.dispatch("ChatMessageAppended", {
        chatSessionId: "s1",
        turnId: "t2",
        message: message(2, "user", "Next"),
      });
    });

    expect(documentOrder(["Done.", "Sharper title", "Next"])).toEqual([
      "Done.",
      "Sharper title",
      "Next",
    ]);
    expect(
      (screen.getByLabelText("Rejection reason (optional)") as HTMLTextAreaElement).value,
    ).toBe("Too long");
  });
});
