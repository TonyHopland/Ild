import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router";
import type { AiProvider, ChatMessage, ChatSession, User, WorkItemEditProposal } from "../../types";

const { chatService, aiProviderService, workItemService, authService } = vi.hoisted(() => ({
  chatService: {
    listHistory: vi.fn(),
    getById: vi.fn(),
    start: vi.fn(),
    sendMessage: vi.fn(),
    interrupt: vi.fn(),
    deleteOne: vi.fn(),
    deleteMany: vi.fn(),
    searchChats: vi.fn(() => Promise.resolve<string[]>([])),
    markRead: vi.fn(() => Promise.resolve()),
    rename: vi.fn(() => Promise.resolve()),
    setFavorite: vi.fn(() => Promise.resolve()),
  },
  aiProviderService: { getAll: vi.fn() },
  workItemService: { listEditProposalsFor: vi.fn() },
  authService: {
    getUser: vi.fn(),
    getToken: vi.fn(() => "test-token"),
    getMe: vi.fn(),
    clearAuth: vi.fn(),
    logout: vi.fn(() => Promise.resolve()),
    onTokenChange: vi.fn(() => () => {}),
  },
}));

vi.mock("../../hooks/useSignalR", async () =>
  (await import("../../test-support")).fakeSignalRModule(),
);

vi.mock("../../services/auth", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../services/auth")>()),
  chatService,
  aiProviderService,
  workItemService,
  authService,
}));
vi.mock("../../utils/openLoopDocument", () => ({ getOpenLoopDocument: vi.fn(() => null) }));

import { ChatInboxProvider } from "../../components/ChatInbox";
import { AuthContext } from "../../hooks/useAuth";
import ChatPage from ".";
import {
  documentOrder,
  makeGlobalVariableProposal,
  reply,
  signalRHub,
  stubFetch,
} from "../../test-support";
import { WorkItemHubProvider } from "../../hooks/useWorkItemHub";

const user: User = { id: "1", username: "test", createdAt: "" };

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

function msg(sequence: number, role: "user" | "assistant", content: string): ChatMessage {
  return {
    id: `m${sequence}`,
    role,
    content,
    interrupted: false,
    sequence,
    createdAt: "2026-01-01T00:00:00Z",
  };
}

const chat: ChatSession = {
  id: "s1",
  name: "Tidy the rules",
  aiProviderId: "p1",
  providerType: "claude-code",
  tools: ["ild"],
  createdAt: "2026-01-01T00:00:00Z",
  messages: [
    msg(0, "user", "Question one."),
    msg(1, "assistant", "Reply one."),
    msg(2, "user", "Question two."),
    msg(3, "assistant", "Reply two."),
  ],
  activeTurnId: null,
};

let suggested = 0;

/** A suggestion made by chat s1 after the reply with sequence `anchor`, told apart by its rationale. */
function suggestion(rationale: string, anchor: number | null, createdAt: string) {
  return makeGlobalVariableProposal({
    id: `gp-${++suggested}`,
    value: `Suggested text number ${suggested}`,
    rationale,
    createdAt,
    snapshotUpdatedAt: "2026-09-01T00:00:00Z",
    currentValue: "Keep reviews short.",
    createdByLoopRunId: null,
    createdByRunNodeId: null,
    createdByChatSessionId: "s1",
    chatReplySequence: anchor,
    source: { kind: "chat", workItemId: null, chatSessionId: "s1", linkable: true },
  });
}

function editProposal(title: string, anchor: number, createdAt: string): WorkItemEditProposal {
  return {
    id: `p-${title}`,
    workItemId: "wi-9",
    status: "Pending",
    proposed: { title },
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
    chatReplySequence: anchor,
    createdAt,
    decidedAt: null,
  };
}

/** What GET /global-variables/proposals?chatSessionId=s1 answers; a test changes it. */
let served: unknown[] = [];
const askedFor: (string | null)[] = [];

beforeEach(() => {
  served = [];
  askedFor.length = 0;
  chatService.listHistory.mockResolvedValue([
    {
      id: "s1",
      name: "Tidy the rules",
      createdAt: "2026-01-01T00:00:00Z",
      updatedAt: "2026-01-02T00:00:00Z",
      hasUnread: false,
    },
  ]);
  chatService.getById.mockImplementation(() => Promise.resolve(structuredClone(chat)));
  aiProviderService.getAll.mockResolvedValue([provider]);
  workItemService.listEditProposalsFor.mockResolvedValue([]);
  authService.getUser.mockReturnValue(user);
  authService.getMe.mockResolvedValue(user);
  stubFetch((call) => {
    if (call.path !== "/api/v1/global-variables/proposals") throw new Error("offline");
    askedFor.push(call.query.get("chatSessionId"));
    return reply(200, call.query.get("chatSessionId") === "s1" ? served : []);
  });
});

afterEach(() => {
  cleanup();
  signalRHub.reset();
  vi.clearAllMocks();
  vi.unstubAllGlobals();
  localStorage.clear();
});

function renderChat(path: string) {
  const auth = {
    user,
    token: "test-token",
    isAuthenticated: true,
    isLoading: false,
    login: vi.fn(),
    logout: vi.fn(),
  };
  return render(
    <MemoryRouter initialEntries={[path]}>
      <AuthContext.Provider value={auth}>
        <ChatInboxProvider>
          <Routes>
            <Route path="/chat/:chatId" element={<ChatPage />} />
          </Routes>
        </ChatInboxProvider>
      </AuthContext.Provider>
    </MemoryRouter>,
    { wrapper: WorkItemHubProvider },
  );
}

describe("global variable suggestions in a chat", () => {
  test("each sits after the reply of the turn that made it, in time order with the chat's edit proposals, and a hint brings in a new one", async () => {
    served = [
      suggestion("Second turn's suggestion", 3, "2026-09-26T10:30:00Z"),
      suggestion("Later after reply one", 1, "2026-09-26T10:05:00Z"),
      suggestion("Earlier after reply one", 1, "2026-09-26T09:55:00Z"),
    ];
    workItemService.listEditProposalsFor.mockResolvedValue([
      editProposal("Edit proposal title", 1, "2026-09-26T10:00:00Z"),
    ]);

    renderChat("/chat/s1");

    await screen.findByText("Second turn's suggestion", { exact: false });
    await screen.findByText("Edit proposal title", { exact: false });
    expect(askedFor).toContain("s1");
    const expected = [
      "Question one.",
      "Reply one.",
      "Earlier after reply one",
      "Edit proposal title",
      "Later after reply one",
      "Question two.",
      "Reply two.",
      "Second turn's suggestion",
    ];
    expect(documentOrder(expected)).toEqual(expected);

    served = [...served, suggestion("Made just now", 3, "2026-09-26T10:40:00Z")];
    await signalRHub.emit("ChatGlobalVariableProposalsChanged", { chatSessionId: "s1-other" });
    await signalRHub.emit("ChatGlobalVariableProposalsChanged", { chatSessionId: "s1" });

    await screen.findByText("Made just now", { exact: false });
  });

  test("a link to a suggestion opens the chat scrolled to its card", async () => {
    const target = suggestion("The linked suggestion", 1, "2026-09-26T10:00:00Z");
    served = [target, suggestion("Another suggestion", 3, "2026-09-26T10:30:00Z")];
    const scrolled: Element[] = [];
    const original = Object.getOwnPropertyDescriptor(Element.prototype, "scrollIntoView");
    Element.prototype.scrollIntoView = vi.fn(function (this: Element) {
      scrolled.push(this);
    });
    try {
      renderChat(`/chat/s1?globalProposal=${target.id}`);

      const rationale = await screen.findByText("The linked suggestion", { exact: false });
      await waitFor(() =>
        expect(
          scrolled.some(
            (el) =>
              el.contains(rationale) &&
              !(el.textContent ?? "").includes("Another suggestion") &&
              !(el.textContent ?? "").includes("Reply one."),
          ),
        ).toBe(true),
      );
    } finally {
      if (original) Object.defineProperty(Element.prototype, "scrollIntoView", original);
      else delete (Element.prototype as { scrollIntoView?: unknown }).scrollIntoView;
    }
  });
});
