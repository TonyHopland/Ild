import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, act } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router";
import type { AiProvider, ChatMessage, ChatSession, User } from "../../types";

const { handlers, invoke, chatService, aiProviderService, workItemService, authService } =
  vi.hoisted(() => ({
    handlers: {} as Record<string, Set<(msg: { payload: unknown }) => void>>,
    invoke: vi.fn((..._args: unknown[]) => Promise.resolve()),
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

vi.mock("../../hooks/useSignalR", () => {
  const on = (event: string, handler: (msg: { payload: unknown }) => void) => {
    (handlers[event] ??= new Set()).add(handler);
  };
  const off = (event: string, handler: (msg: { payload: unknown }) => void) => {
    handlers[event]?.delete(handler);
  };
  return { useSignalR: () => ({ connectionState: "connected", on, off, invoke }) };
});

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
  return {
    id: `lp-${++proposed}`,
    templateId: "tpl-1",
    templateName: "Release Loop",
    templateExists: true,
    baseVersion: 3,
    latestVersion: 3,
    rationale,
    status: "Pending",
    editedBeforeSave: false,
    appliedVersion: null,
    rejectionReason: null,
    createdAt,
    decidedAt: null,
    createdByLoopRunId: null,
    createdByRunNodeId: null,
    createdByChatSessionId: "s1",
    chatReplySequence: anchor,
    source: { kind: "chat", workItemId: null, chatSessionId: "s1", linkable: true },
    changes: {
      nameChanged: false,
      descriptionChanged: false,
      recoveryPolicyChanged: false,
      nodesAdded: [{ id: "n-lint", label: "Lint", type: "Cmd" }],
      nodesRemoved: [],
      nodesChanged: [],
      edgesAdded: [],
      edgesRemoved: [],
    },
  };
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
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: string) => {
      const url = new URL(input, "http://localhost");
      if (url.pathname !== "/api/v1/loop-template-proposals") throw new Error("offline");
      askedFor.push(url.searchParams.get("chatSessionId"));
      const body = url.searchParams.get("chatSessionId") === "s1" ? served : [];
      return {
        ok: true,
        status: 200,
        statusText: "",
        text: () => Promise.resolve(JSON.stringify(body)),
      } as Response;
    }),
  );
});

afterEach(() => {
  cleanup();
  for (const k of Object.keys(handlers)) delete handlers[k];
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

function emit(event: string, payload: unknown) {
  return act(async () => {
    for (const handler of Array.from(handlers[event] ?? [])) handler({ payload });
  });
}

/** The labels, in the order their elements appear in the document. */
function documentOrder(labels: string[]): string[] {
  return labels
    .map((l) => [l, screen.getByText(l, { exact: false })] as const)
    .sort(([, a], [, b]) =>
      a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1,
    )
    .map(([l]) => l);
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
    await emit("ChatLoopTemplateProposalsChanged", { chatSessionId: "s1" });

    await screen.findByText("Made just now", { exact: false });
  });
});
