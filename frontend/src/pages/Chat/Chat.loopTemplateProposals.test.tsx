import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router";
import type { AiProvider, ChatMessage, ChatSession, User } from "../../types";

const { chatService, aiProviderService, workItemService, authService } = vi.hoisted(() => ({
  chatService: {
    listHistory: vi.fn(),
    getById: vi.fn(),
    start: vi.fn(),
    sendMessage: vi.fn(),
    interrupt: vi.fn(),
    deleteOne: vi.fn(),
    deleteAll: vi.fn(),
    searchChats: vi.fn(() => Promise.resolve([] as string[])),
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
  makeLoopTemplateProposal,
  reply,
  signalRHub,
  stubFetch,
} from "../../test-support";

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
  name: "Retro",
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

let proposed = 0;

/** A proposal made by chat s1 after the reply with sequence `anchor`, told apart by its rationale. */
function proposal(rationale: string, anchor: number | null, createdAt: string) {
  return makeLoopTemplateProposal({
    id: `lp-${++proposed}`,
    rationale,
    createdAt,
    createdByLoopRunId: null,
    createdByRunNodeId: null,
    createdByChatSessionId: "s1",
    chatReplySequence: anchor,
    source: { kind: "chat", workItemId: null, chatSessionId: "s1", linkable: true },
  });
}

/** What GET /loop-template-proposals?chatSessionId=s1 answers; a test changes it. */
let served: unknown[] = [];
const askedFor: (string | null)[] = [];

beforeEach(() => {
  served = [];
  askedFor.length = 0;
  chatService.listHistory.mockResolvedValue([
    {
      id: "s1",
      name: "Retro",
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
    if (call.path !== "/api/v1/loop-template-proposals") throw new Error("offline");
    askedFor.push(call.query.get("chatSessionId"));
    return reply(200, call.query.get("chatSessionId") === "s1" ? served : []);
  });
});

afterEach(() => {
  cleanup();
  for (const k of Object.keys(signalRHub.handlers)) delete signalRHub.handlers[k];
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
  );
}

describe("loop template proposals in a chat", () => {
  test("each sits inline after the reply of the turn that made it, and a hint for this chat brings in a new one", async () => {
    served = [
      proposal("Second turn's proposal", 3, "2026-09-26T10:30:00Z"),
      proposal("First turn's proposal", 1, "2026-09-26T10:05:00Z"),
    ];

    renderChat("/chat/s1");

    await screen.findByText("Second turn's proposal", { exact: false });
    expect(askedFor).toContain("s1");
    const expected = [
      "Question one.",
      "Reply one.",
      "First turn's proposal",
      "Question two.",
      "Reply two.",
      "Second turn's proposal",
    ];
    expect(documentOrder(expected)).toEqual(expected);
    expect(screen.getAllByRole("button", { name: "Save" })).toHaveLength(2);

    served = [...served, proposal("Made just now", 3, "2026-09-26T10:40:00Z")];
    await signalRHub.emit("ChatLoopTemplateProposalsChanged", { chatSessionId: "s1" });

    await screen.findByText("Made just now", { exact: false });
  });
});
