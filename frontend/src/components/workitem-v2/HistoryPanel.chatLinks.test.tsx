import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, cleanup, act, screen, fireEvent } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useParams } from "react-router";

vi.mock("../../hooks/useSignalR", async () =>
  (await import("../../test-support")).fakeSignalRModule(),
);

import HistoryPanel from "./HistoryPanel";
import { ChatInboxProvider } from "../ChatInbox";
import { WorkItemStatus } from "../../types";
import type { WorkItem, WorkItemHistoryEntry } from "../../types";
import * as authServices from "../../services/auth";
import { chatSummary } from "../../test-support.chatBubble";
import { deferred } from "../../test-support";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const CHAT_ID = "3f2b8c1e-9a4d-4e57-8b6a-1c2d3e4f5a6b";
const OTHER_ID = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";

const workItem: WorkItem = {
  id: "wi-1",
  title: "Test Work Item",
  description: "",
  status: WorkItemStatus.Backlog,
  priority: 0,
  tags: [],
  repositoryId: "repo-1",
  prUrl: null,
  pullRequestBranch: null,
  humanFeedbackReason: null,
  humanFeedbackActions: null,
  createdAt: "2025-01-01T00:00:00Z",
  startedAt: null,
  completedAt: null,
  currentLoopRunId: null,
  latestLoopRunId: null,
  dependencyIds: [],
  dependentIds: [],
};

function entry(overrides: Partial<WorkItemHistoryEntry>): WorkItemHistoryEntry {
  return {
    saveId: "save-1",
    timestamp: new Date(Date.now() - 3 * 60 * 60 * 1000).toISOString(),
    kind: "Changed",
    username: "bob",
    actorKind: "Chat",
    actorDetail: `chat ${CHAT_ID}`,
    actorWorkItemId: null,
    field: "title",
    oldValue: "Old",
    newValue: "New",
    reason: null,
    ...overrides,
  } as WorkItemHistoryEntry;
}

function Landing() {
  const { chatId } = useParams();
  return <div data-testid="chat-page">chat page {chatId}</div>;
}

function TaskboardLanding() {
  const { id } = useParams();
  return <div data-testid="taskboard-page">item {id}</div>;
}

async function renderPanel(
  history: WorkItemHistoryEntry[],
  chats: Promise<ReturnType<typeof chatSummary>[]> | ReturnType<typeof chatSummary>[],
) {
  vi.spyOn(authServices.workItemService, "getHistory").mockResolvedValue(history);
  vi.spyOn(authServices.chatService, "listHistory").mockImplementation(() =>
    Promise.resolve(chats),
  );
  await act(async () => {
    render(
      <MemoryRouter initialEntries={["/workitem"]}>
        <ChatInboxProvider>
          <Routes>
            <Route
              path="/workitem"
              element={<HistoryPanel workItem={workItem} active repositoryName={() => "repo"} />}
            />
            <Route path="/chat/:chatId" element={<Landing />} />
            <Route path="/taskboard/:id" element={<TaskboardLanding />} />
          </Routes>
        </ChatInboxProvider>
      </MemoryRouter>,
    );
    await Promise.resolve();
  });
}

const detailLink = (detail: string) => screen.queryByRole("link", { name: detail });

describe("HistoryPanel chat references", () => {
  test("an own chat's reference is a link that opens that chat on the Chat page", async () => {
    await renderPanel([entry({})], [chatSummary(CHAT_ID, "Mine")]);
    const link = await screen.findByRole("link", { name: `chat ${CHAT_ID}` });
    expect(link.getAttribute("href")).toBe(`/chat/${CHAT_ID}`);
    fireEvent.click(link);
    expect((await screen.findByTestId("chat-page")).textContent).toBe(`chat page ${CHAT_ID}`);
  });

  test.each(["Created", "Baseline", "Deleted"] as const)(
    "a %s save with a chat actor is linked too",
    async (kind) => {
      await renderPanel(
        [entry({ kind, field: kind === "Deleted" ? "deleted" : "title", oldValue: null })],
        [chatSummary(CHAT_ID, "Mine")],
      );
      expect(await screen.findByRole("link", { name: `chat ${CHAT_ID}` })).toBeTruthy();
    },
  );

  test("a chat that is not in the list stays plain text", async () => {
    await renderPanel([entry({})], [chatSummary(OTHER_ID, "Other")]);
    expect(await screen.findByText(`chat ${CHAT_ID}`)).toBeTruthy();
    expect(detailLink(`chat ${CHAT_ID}`)).toBeNull();
    expect(screen.queryAllByRole("link")).toHaveLength(0);
  });

  test("a chat list that fails to load leaves plain text", async () => {
    vi.spyOn(authServices.workItemService, "getHistory").mockResolvedValue([entry({})]);
    vi.spyOn(authServices.chatService, "listHistory").mockRejectedValue(new Error("down"));
    await act(async () => {
      render(
        <MemoryRouter>
          <ChatInboxProvider>
            <HistoryPanel workItem={workItem} active repositoryName={() => "repo"} />
          </ChatInboxProvider>
        </MemoryRouter>,
      );
      await Promise.resolve();
    });
    expect(await screen.findByText(`chat ${CHAT_ID}`)).toBeTruthy();
    expect(screen.queryAllByRole("link")).toHaveLength(0);
  });

  test("the reference is plain until the list loads, then becomes a link without reopening", async () => {
    const list = deferred<ReturnType<typeof chatSummary>[]>();
    await renderPanel([entry({})], list.promise);
    expect(await screen.findByText(`chat ${CHAT_ID}`)).toBeTruthy();
    expect(detailLink(`chat ${CHAT_ID}`)).toBeNull();
    await act(async () => {
      list.resolve([chatSummary(CHAT_ID, "Mine")]);
      await list.promise;
    });
    expect(await screen.findByRole("link", { name: `chat ${CHAT_ID}` })).toBeTruthy();
  });

  test.each([
    ["Agent", `chat ${CHAT_ID}`],
    ["System", `chat ${CHAT_ID}`],
    ["Human", `chat ${CHAT_ID}`],
    ["Chat", `chat ${CHAT_ID} extra`],
    ["Chat", `chat not-a-guid`],
    ["Chat", `chat ${CHAT_ID}, chat ${OTHER_ID}`],
    ["Chat", `a chat ${CHAT_ID}`],
    ["Chat", CHAT_ID],
  ] as const)("kind %s with detail %j is not linked", async (actorKind, actorDetail) => {
    await renderPanel(
      [entry({ actorKind, actorDetail })],
      [chatSummary(CHAT_ID, "Mine"), chatSummary(OTHER_ID, "Other")],
    );
    expect(await screen.findByText(actorDetail)).toBeTruthy();
    expect(screen.queryAllByRole("link")).toHaveLength(0);
  });

  test("an upper-case id in the detail matches the inbox id, and the link uses the inbox's form", async () => {
    await renderPanel(
      [entry({ actorDetail: `chat ${CHAT_ID.toUpperCase()}` })],
      [chatSummary(CHAT_ID, "Mine")],
    );
    const link = await screen.findByRole("link", { name: `chat ${CHAT_ID.toUpperCase()}` });
    expect(link.getAttribute("href")).toBe(`/chat/${CHAT_ID}`);
  });

  test("an agent save's working-on link opens that work item", async () => {
    await renderPanel(
      [entry({ actorKind: "Agent", actorDetail: "run r1", actorWorkItemId: "42" })],
      [],
    );
    const link = await screen.findByRole("link", { name: "#42" });
    expect(link.getAttribute("href")).toBe("/taskboard/42");
    fireEvent.click(link);
    expect((await screen.findByTestId("taskboard-page")).textContent).toBe("item 42");
  });
});
