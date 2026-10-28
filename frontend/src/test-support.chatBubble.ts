// Helpers for the chat bubble's tests. Not a test file itself, so it is never
// collected as a suite.
import { createElement, type ReactElement } from "react";
import { MemoryRouter } from "react-router";
import { fireEvent, render, screen, within } from "@testing-library/react";
import { beforeEach, vi, type MockInstance } from "vite-plus/test";
import {
  aiProviderService,
  chatService,
  globalVariableService,
  loopTemplateProposalService,
  workItemService,
} from "./services/auth";
import type { ChatMessage, ChatSession, ChatSessionSummary } from "./types";
import {
  deferred,
  emitHubEvent,
  openChatFromList,
  openChatList,
  type ChatHubEvents,
  type Deferred,
} from "./test-support";

/** Delivers a chat turn event with the payload the server really sends for it. */
export function emitChatEvent<E extends keyof ChatHubEvents>(event: E, payload: ChatHubEvents[E]) {
  emitHubEvent(event, payload);
}

/** A chat in the history list; `extra` sets the fields a test cares about. */
export function chatSummary(
  id: string,
  name: string | null,
  extra: Partial<ChatSessionSummary> = {},
): ChatSessionSummary {
  return {
    id,
    name,
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-02T00:00:00Z",
    hasUnread: false,
    ...extra,
  };
}

/** An empty assistant message with a random id; `partial` sets the fields a test cares about. */
export function chatMessage(partial: Partial<ChatMessage> = {}): ChatMessage {
  return {
    id: crypto.randomUUID(),
    role: "assistant",
    content: "",
    interrupted: false,
    sequence: 0,
    createdAt: "2026-01-01T00:00:00Z",
    ...partial,
  };
}

/** A chat as the server returns it when opened, "Past chat" with no messages by default; `partial` sets the fields a test cares about. */
export function chatSession(partial: Partial<ChatSession> = {}): ChatSession {
  return {
    id: "s1",
    name: "Past chat",
    aiProviderId: "p1",
    providerType: "claude-code",
    tools: ["ild"],
    createdAt: "2026-01-01T00:00:00Z",
    messages: [],
    activeTurnId: null,
    ...partial,
  };
}

/** Opens the bubble with `session` as the only chat in the history and resumes it from the list. */
export async function openResumedChat(session: ChatSession, initialPath = "/") {
  vi.mocked(chatService.listHistory).mockResolvedValue([chatSummary(session.id, session.name)]);
  vi.mocked(chatService.getById).mockResolvedValue(session);
  const view = await renderChatBubble(initialPath);
  await openChatPanel();
  await resumeChat(session.name ?? "Untitled chat");
  return view;
}

/** Spies on every method {@link stubChatServices} stands in for, by service. */
function spyOnChatServices() {
  return {
    chatService: {
      listHistory: vi.spyOn(chatService, "listHistory"),
      getById: vi.spyOn(chatService, "getById"),
      start: vi.spyOn(chatService, "start"),
      sendMessage: vi.spyOn(chatService, "sendMessage"),
      interrupt: vi.spyOn(chatService, "interrupt"),
      markRead: vi.spyOn(chatService, "markRead"),
      rename: vi.spyOn(chatService, "rename"),
      setFavorite: vi.spyOn(chatService, "setFavorite"),
      searchChats: vi.spyOn(chatService, "searchChats"),
      deleteOne: vi.spyOn(chatService, "deleteOne"),
      deleteMany: vi.spyOn(chatService, "deleteMany"),
    },
    aiProviderService: {
      getAll: vi.spyOn(aiProviderService, "getAll"),
    },
    proposalServices: {
      listVariableProposals: vi.spyOn(globalVariableService, "listProposals"),
      listTemplateProposals: vi.spyOn(loopTemplateProposalService, "list"),
    },
    workItemService: {
      listEditProposals: vi.spyOn(workItemService, "listEditProposals"),
      listEditProposalsFor: vi.spyOn(workItemService, "listEditProposalsFor"),
      approveEditProposal: vi.spyOn(workItemService, "approveEditProposal"),
      rejectEditProposal: vi.spyOn(workItemService, "rejectEditProposal"),
    },
  };
}

/**
 * Spies on the chat, provider and edit-proposal methods the chat bubble calls,
 * each answering with nothing (an empty list for the list reads) until a test
 * says otherwise. Call it once at the top of a test file. Before every test the
 * spies are put back to those answers with every call forgotten, and spied on
 * afresh, so a test that restores mocks leaves the next one stubbed all the
 * same. Each service's spies stay on the one object, so a test file can hold it.
 */
export function stubChatServices() {
  const spies = spyOnChatServices();
  const reset = () => {
    const fresh = spyOnChatServices();
    const listReads: MockInstance[] = [
      fresh.chatService.listHistory,
      fresh.chatService.searchChats,
      fresh.aiProviderService.getAll,
      fresh.proposalServices.listVariableProposals,
      fresh.proposalServices.listTemplateProposals,
      fresh.workItemService.listEditProposals,
      fresh.workItemService.listEditProposalsFor,
    ];
    for (const group of Object.keys(fresh) as (keyof typeof fresh)[]) {
      Object.assign(spies[group], fresh[group]);
      for (const spy of Object.values(fresh[group]) as MockInstance[]) {
        spy.mockReset();
        spy.mockResolvedValue(listReads.includes(spy) ? [] : undefined);
      }
    }
  };
  reset();
  beforeEach(reset);
  return spies;
}

/** A search request held back by {@link chatServer} until the test answers it. */
interface HeldSearch {
  q: string;
  answer: Deferred<string[]>;
}

/**
 * A stateful stand-in for the chat endpoints behind the spies of
 * {@link stubChatServices}: `chats` is what the history answers right now and
 * `sessions` what opening a chat returns; mark-read, rename, favourite and delete
 * change `chats` as the server would. `holdHistoryReads(n)` leaves the next `n`
 * history reads in flight (in `heldReads`) and every search is held in `searches`.
 * Before every test the server is emptied and answers through the fresh spies.
 */
export function chatServer(services: ReturnType<typeof stubChatServices>) {
  const { chatService: api } = services;
  const server = {
    chats: [] as ChatSessionSummary[],
    sessions: {} as Record<string, ChatSession>,
    heldReads: [] as Deferred<ChatSessionSummary[]>[],
    searches: [] as HeldSearch[],
    readsToHold: 0,
    holdHistoryReads(count: number) {
      server.readsToHold += count;
    },
    update(id: string, change: Partial<ChatSessionSummary>) {
      server.chats = server.chats.map((c) => (c.id === id ? { ...c, ...change } : c));
    },
    install() {
      api.listHistory.mockImplementation(() => {
        if (server.readsToHold > 0) {
          server.readsToHold -= 1;
          const held = deferred<ChatSessionSummary[]>();
          server.heldReads.push(held);
          return held.promise;
        }
        return Promise.resolve(server.chats.map((c) => ({ ...c })));
      });
      api.getById.mockImplementation((id: string) => Promise.resolve(server.sessions[id]));
      api.markRead.mockImplementation((id: string) => {
        server.update(id, { hasUnread: false });
        return Promise.resolve();
      });
      api.rename.mockImplementation((id: string, name: string) => {
        server.update(id, { name });
        return Promise.resolve();
      });
      api.setFavorite.mockImplementation((id: string, isFavorite: boolean) => {
        server.update(id, { isFavorite });
        return Promise.resolve();
      });
      api.deleteOne.mockImplementation((id: string) => {
        server.chats = server.chats.filter((c) => c.id !== id);
        return Promise.resolve();
      });
      api.deleteMany.mockImplementation((ids: string[]) => {
        server.chats = server.chats.filter((c) => !ids.includes(c.id));
        return Promise.resolve();
      });
      api.searchChats.mockImplementation((q: string) => {
        const answer = deferred<string[]>();
        server.searches.push({ q, answer });
        return answer.promise;
      });
      api.sendMessage.mockResolvedValue("t1");
    },
    reset() {
      server.chats = [];
      for (const id of Object.keys(server.sessions)) delete server.sessions[id];
      server.heldReads = [];
      server.searches = [];
      server.readsToHold = 0;
      server.install();
    },
  };
  server.install();
  beforeEach(() => server.reset());
  return server;
}

/** The chat bubble in a router and the chat inbox, as the app mounts it. */
export async function chatBubbleTree(initialPath = "/"): Promise<ReactElement> {
  const [{ default: ChatBubble }, { ChatInboxProvider }] = await Promise.all([
    import("./components/ChatBubble"),
    import("./components/ChatInbox"),
  ]);
  return createElement(
    MemoryRouter,
    { initialEntries: [initialPath] },
    createElement(ChatInboxProvider, null, createElement(ChatBubble)),
  );
}

/** Renders {@link chatBubbleTree} under the shared work-item hub; rerender with `await chatBubbleTree()`. */
export async function renderChatBubble(initialPath = "/") {
  const { WorkItemHubProvider } = await import("./hooks/useWorkItemHub");
  return render(await chatBubbleTree(initialPath), { wrapper: WorkItemHubProvider });
}

/** Opens the chat bubble's panel from its button. */
export async function openChatPanel(): Promise<void> {
  fireEvent.click(await screen.findByRole("button", { name: "Open chat" }));
}

/** Opens a chat from the list and waits for its message box. */
export async function resumeChat(name: string): Promise<void> {
  await openChatFromList(name);
  await screen.findByLabelText("Chat message");
}

/** Leaves the open chat for the start form, from the chat bubble's list. */
export async function startNewChat(): Promise<void> {
  fireEvent.click(within(await openChatList()).getByRole("button", { name: "New chat" }));
}

/** Leaves the open chat, then shows the list again. */
export async function backToChatList(): Promise<void> {
  await startNewChat();
  await screen.findByText("Start chat");
  await openChatList();
}
