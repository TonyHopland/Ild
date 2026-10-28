import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { screen, fireEvent, waitFor, act, within } from "@testing-library/react";
import type { ChatMessageAppendedPayload, ChatSession, ChatUnreadChangedPayload } from "../types";
import { setChatEnabled } from "../hooks/useChatEnabled";
import {
  backToChatList,
  chatBubbleTree,
  chatMessage,
  chatServer,
  chatSession,
  chatSummary,
  deferred,
  emitHubEvent,
  openChatList,
  openChatPanel,
  renderChatBubble,
  resumeChat,
  settle,
  showChatActions,
  signalRHub,
  stubChatServices,
} from "../test-support";

vi.mock("../hooks/useSignalR", async () => (await import("../test-support")).fakeSignalRModule());
vi.mock("../utils/openLoopDocument", () => ({ getOpenLoopDocument: vi.fn() }));
vi.mock("../services/chatSessionStore", () => ({ setCurrentChatSessionId: vi.fn() }));

const services = stubChatServices();
const { chatService } = services;
const server = chatServer(services);

afterEach(() => server.reset());

function emitAppended(payload: ChatMessageAppendedPayload) {
  emitHubEvent("ChatMessageAppended", payload);
}

function emitUnreadChanged(payload: ChatUnreadChangedPayload) {
  emitHubEvent("ChatUnreadChanged", payload);
}

function fab() {
  return screen.getByRole("button", { name: "Open chat" });
}

function fabDot() {
  return within(fab()).queryByRole("img", { name: "New messages" });
}

function row(name: string) {
  return screen.getByText(name).closest("li") as HTMLElement;
}

function rowDot(name: string) {
  return within(row(name)).queryByRole("img", { name: "New messages" });
}

function closePanel() {
  fireEvent.click(screen.getByLabelText("Close chat"));
}

async function send(text: string) {
  fireEvent.change(screen.getByLabelText("Chat message"), { target: { value: text } });
  fireEvent.click(screen.getByText("Send"));
  await waitFor(() => expect(chatService.sendMessage).toHaveBeenCalled());
  await settle();
}

describe("unread chat indicator", () => {
  test("after a reload the closed button and each unread row carry the dot, read rows none", async () => {
    server.chats = [chatSummary("a", "Alpha", { hasUnread: true }), chatSummary("b", "Beta")];

    await renderChatBubble();

    await waitFor(() => expect(fabDot()).not.toBeNull());
    // The dot adds to the button without renaming it.
    expect(fab().getAttribute("aria-label")).toBe("Open chat");
    expect(screen.queryAllByRole("status")).toHaveLength(0);

    await openChatPanel();
    await openChatList();
    await screen.findByText("Beta");
    expect(rowDot("Alpha")).not.toBeNull();
    expect(rowDot("Beta")).toBeNull();
    expect(screen.queryAllByRole("status")).toHaveLength(0);
  });

  test("send, close: the reply lights the button live, and reopening reads it and clears the dot", async () => {
    server.chats = [chatSummary("a", "Alpha")];
    server.sessions.a = chatSession("a", "Alpha", [
      chatMessage("a", 0, "user"),
      chatMessage("a", 1, "assistant"),
    ]);
    await renderChatBubble();
    await openChatPanel();
    await resumeChat("Alpha");

    await send("do the thing");
    emitAppended({ chatSessionId: "a", turnId: "t1", message: chatMessage("a", 2, "user") });
    closePanel();

    // The reply is finalized while the panel is closed.
    emitAppended({ chatSessionId: "a", turnId: "t1", message: chatMessage("a", 3, "assistant") });
    server.update("a", { hasUnread: true });
    emitUnreadChanged({ chatSessionId: "a" });

    await waitFor(() => expect(fabDot()).not.toBeNull());
    expect(chatService.markRead).not.toHaveBeenCalledWith("a", 3);

    await openChatPanel();
    // Still the chat that was open, now read up to the reply.
    expect(within(screen.getByRole("dialog")).getByText("Alpha")).toBeTruthy();
    expect(screen.getByLabelText("Chat message")).toBeTruthy();
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("a", 3));
    await settle();

    closePanel();
    await waitFor(() => expect(fabDot()).toBeNull());
  });

  test("a reply seen in the open chat is marked read, and a history read taken before that settled cannot bring the dot back", async () => {
    server.chats = [chatSummary("a", "Alpha")];
    server.sessions.a = chatSession("a", "Alpha", [chatMessage("a", 0, "user")]);
    await renderChatBubble();
    await openChatPanel();
    await resumeChat("Alpha");
    await settle();

    const marking = deferred<void>();
    chatService.markRead.mockImplementationOnce(() => marking.promise);
    server.holdHistoryReads(2);

    // The reply lands and its hint's history read goes out while the reply is unread.
    emitAppended({ chatSessionId: "a", turnId: "t1", message: chatMessage("a", 1, "assistant") });
    server.update("a", { hasUnread: true });
    emitUnreadChanged({ chatSessionId: "a" });
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("a", 1));
    await waitFor(() => expect(server.heldReads).toHaveLength(1));

    // The mark-read settles; whatever history read follows it stays in flight.
    server.update("a", { hasUnread: false });
    await act(async () => marking.resolve());
    await waitFor(() => expect(server.heldReads).toHaveLength(2));

    // The snapshot from before the mark-read settled answers first, still saying unread.
    await act(async () =>
      server.heldReads[0].resolve([chatSummary("a", "Alpha", { hasUnread: true })]),
    );
    await settle();
    closePanel();
    expect(fabDot()).toBeNull();

    await act(async () => server.heldReads[1].resolve([chatSummary("a", "Alpha")]));
    await settle();
    expect(fabDot()).toBeNull();
  });

  test("a reply for a chat left for the list or for another chat lights that chat's row and the button, never the other chat", async () => {
    server.chats = [chatSummary("a", "Alpha"), chatSummary("b", "Beta")];
    server.sessions.a = chatSession("a", "Alpha", [
      chatMessage("a", 0, "user"),
      chatMessage("a", 1, "assistant"),
    ]);
    server.sessions.b = chatSession("b", "Beta", [
      chatMessage("b", 0, "user"),
      chatMessage("b", 1, "assistant"),
    ]);
    await renderChatBubble();
    await openChatPanel();
    await resumeChat("Alpha");
    await send("question for A");
    emitAppended({ chatSessionId: "a", turnId: "t1", message: chatMessage("a", 2, "user") });
    await backToChatList();

    // A's reply lands while the list is on screen.
    server.update("a", { hasUnread: true });
    emitUnreadChanged({ chatSessionId: "a" });
    await waitFor(() => expect(rowDot("Alpha")).not.toBeNull());
    expect(rowDot("Beta")).toBeNull();

    // Opening B reads B, not A.
    await resumeChat("Beta");
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("b", 1));
    await settle();
    closePanel();
    await waitFor(() => expect(fabDot()).not.toBeNull());
    expect(chatService.markRead.mock.calls.filter(([id, seq]) => id === "a" && seq >= 3)).toEqual(
      [],
    );

    await openChatPanel();
    await backToChatList();
    await waitFor(() => expect(rowDot("Alpha")).not.toBeNull());
    expect(rowDot("Beta")).toBeNull();
  });

  test("opening an unread chat from the list reads it up to its newest message, and other unread chats keep their dots", async () => {
    server.chats = [
      chatSummary("a", "Alpha", { hasUnread: true }),
      chatSummary("b", "Beta", { hasUnread: true }),
    ];
    server.sessions.a = chatSession("a", "Alpha", [
      chatMessage("a", 0, "user"),
      chatMessage("a", 1, "assistant"),
      chatMessage("a", 2, "user"),
      chatMessage("a", 3, "assistant"),
    ]);
    server.sessions.b = chatSession("b", "Beta", [
      chatMessage("b", 4, "user"),
      chatMessage("b", 5, "assistant"),
    ]);
    await renderChatBubble();
    await openChatPanel();
    await openChatList();
    await screen.findByText("Beta");
    expect(rowDot("Alpha")).not.toBeNull();
    expect(rowDot("Beta")).not.toBeNull();

    await resumeChat("Alpha");
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("a", 3));
    await settle();
    await backToChatList();
    await waitFor(() => expect(rowDot("Alpha")).toBeNull());
    expect(rowDot("Beta")).not.toBeNull();
    closePanel();
    expect(fabDot()).not.toBeNull();

    await openChatPanel();
    await resumeChat("Beta");
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("b", 5));
    await settle();
    closePanel();
    await waitFor(() => expect(fabDot()).toBeNull());
  });

  test("a failed mark-read shows no error, leaves the chat unread, touches no other chat, and is tried again on the next open", async () => {
    server.chats = [
      chatSummary("a", "Alpha", { hasUnread: true }),
      chatSummary("b", "Beta", { hasUnread: true }),
    ];
    server.sessions.a = chatSession("a", "Alpha", [
      chatMessage("a", 0, "user"),
      chatMessage("a", 1, "assistant"),
    ]);
    server.sessions.b = chatSession("b", "Beta", [
      chatMessage("b", 0, "user"),
      chatMessage("b", 1, "assistant"),
    ]);
    const markingA = deferred<void>();
    chatService.markRead.mockImplementationOnce(() => markingA.promise);
    await renderChatBubble();
    await openChatPanel();

    await resumeChat("Alpha");
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("a", 1));
    await backToChatList();
    await resumeChat("Beta");
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("b", 1));
    await settle();
    await backToChatList();
    await waitFor(() => expect(rowDot("Beta")).toBeNull());

    await act(async () => markingA.reject(new Error("mark-read failed")));
    await settle();

    expect(screen.queryByText("mark-read failed")).toBeNull();
    expect(rowDot("Alpha")).not.toBeNull();
    expect(rowDot("Beta")).toBeNull();

    await resumeChat("Alpha");
    await waitFor(() =>
      expect(chatService.markRead.mock.calls.filter(([id]) => id === "a")).toEqual([
        ["a", 1],
        ["a", 1],
      ]),
    );
    await settle();
    await backToChatList();
    await waitFor(() => expect(rowDot("Alpha")).toBeNull());
  });

  test("a reply that lands while chat is hidden in settings is read only once the chat is shown again", async () => {
    server.chats = [chatSummary("a", "Alpha")];
    server.sessions.a = chatSession("a", "Alpha", [chatMessage("a", 0, "user")]);
    await renderChatBubble();
    await openChatPanel();
    await resumeChat("Alpha");
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("a", 0));

    act(() => setChatEnabled(false));
    emitAppended({ chatSessionId: "a", turnId: "t1", message: chatMessage("a", 1, "assistant") });
    await settle();
    expect(chatService.markRead).not.toHaveBeenCalledWith("a", 1);

    act(() => setChatEnabled(true));
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("a", 1));
  });

  test("a chat covered by the narrow list stays unread while another chat picked there is still opening", async () => {
    server.chats = [chatSummary("a", "Alpha"), chatSummary("b", "Beta")];
    server.sessions.a = chatSession("a", "Alpha", [chatMessage("a", 0, "user")]);
    server.sessions.b = chatSession("b", "Beta", [chatMessage("b", 1, "user")]);
    await renderChatBubble();
    await openChatPanel();
    await resumeChat("Alpha");
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("a", 0));
    await settle();

    // A reply lands in Alpha while the list covers it, and Beta's open is slow.
    const list = await openChatList();
    emitAppended({ chatSessionId: "a", turnId: "t1", message: chatMessage("a", 2, "assistant") });
    const opening = deferred<ChatSession>();
    chatService.getById.mockImplementationOnce(() => opening.promise);
    fireEvent.click(within(list).getByText("Beta"));
    await settle();
    expect(screen.queryByText("assistant message 2")).toBeNull();
    expect(chatService.markRead).not.toHaveBeenCalledWith("a", 2);

    await act(async () => opening.resolve(server.sessions.b));
    expect(await screen.findByText("user message 1")).toBeTruthy();
    await waitFor(() => expect(chatService.markRead).toHaveBeenCalledWith("b", 1));
    expect(chatService.markRead).not.toHaveBeenCalledWith("a", 2);
  });

  test("every (re)connect rejoins the inbox and re-reads history, recovering a hint lost while away", async () => {
    server.chats = [chatSummary("a", "Alpha")];
    const view = await renderChatBubble();
    await waitFor(() => expect(signalRHub.invoke).toHaveBeenCalledWith("SubscribeToChatInbox"));
    await settle();
    expect(fabDot()).toBeNull();

    signalRHub.setState("reconnecting");
    const dropped = await chatBubbleTree();
    act(() => view.rerender(dropped));
    // The reply lands, and its hint is lost, while the connection is down.
    server.update("a", { hasUnread: true });
    signalRHub.setState("connected");
    const restored = await chatBubbleTree();
    act(() => view.rerender(restored));

    await waitFor(() =>
      expect(
        signalRHub.invoke.mock.calls.filter(([method]) => method === "SubscribeToChatInbox"),
      ).toHaveLength(2),
    );
    await waitFor(() => expect(fabDot()).not.toBeNull());
  });

  test("a history read in flight when a chat's delete settles never brings the chat or its dot back", async () => {
    server.chats = [chatSummary("a", "Alpha", { hasUnread: true }), chatSummary("b", "Beta")];
    await renderChatBubble();
    await openChatPanel();
    await openChatList();
    await screen.findByText("Alpha");

    server.holdHistoryReads(1);
    emitUnreadChanged({ chatSessionId: "a" });
    await waitFor(() => expect(server.heldReads).toHaveLength(1));
    showChatActions("Alpha");
    fireEvent.click(screen.getByLabelText("Delete chat Alpha"));
    await waitFor(() => expect(screen.queryByText("Alpha")).toBeNull());

    await act(async () =>
      server.heldReads[0].resolve([
        chatSummary("a", "Alpha", { hasUnread: true }),
        chatSummary("b", "Beta"),
      ]),
    );
    await settle();

    expect(screen.queryByText("Alpha")).toBeNull();
    expect(screen.getByText("Beta")).toBeTruthy();
    closePanel();
    expect(fabDot()).toBeNull();
  });

  test("a history read in flight when a bulk delete settles never brings any chat or dot back", async () => {
    server.chats = [
      chatSummary("a", "Alpha", { hasUnread: true }),
      chatSummary("b", "Beta", { hasUnread: true }),
    ];
    await renderChatBubble();
    await openChatPanel();
    await openChatList();
    await screen.findByText("Alpha");

    server.holdHistoryReads(1);
    emitUnreadChanged({ chatSessionId: "b" });
    await waitFor(() => expect(server.heldReads).toHaveLength(1));
    fireEvent.click(screen.getByRole("button", { name: "Select" }));
    fireEvent.click(screen.getByRole("checkbox", { name: "Select all" }));
    fireEvent.click(screen.getByRole("button", { name: "Delete (2)" }));
    fireEvent.click(
      within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Delete" }),
    );
    await waitFor(() => expect(screen.queryByText("Alpha")).toBeNull());

    await act(async () =>
      server.heldReads[0].resolve([
        chatSummary("a", "Alpha", { hasUnread: true }),
        chatSummary("b", "Beta", { hasUnread: true }),
      ]),
    );
    await settle();

    expect(screen.queryByText("Alpha")).toBeNull();
    expect(screen.queryByText("Beta")).toBeNull();
    closePanel();
    expect(fabDot()).toBeNull();
  });

  test("a hint about another chat in flight when a delete settles still lights that chat", async () => {
    server.chats = [chatSummary("a", "Alpha"), chatSummary("b", "Beta")];
    await renderChatBubble();
    await openChatPanel();
    await openChatList();
    await screen.findByText("Alpha");

    // B's reply lands and its hint's read goes out before A's delete settles.
    server.holdHistoryReads(1);
    server.update("b", { hasUnread: true });
    emitUnreadChanged({ chatSessionId: "b" });
    await waitFor(() => expect(server.heldReads).toHaveLength(1));
    showChatActions("Alpha");
    fireEvent.click(screen.getByLabelText("Delete chat Alpha"));
    await waitFor(() => expect(screen.queryByText("Alpha")).toBeNull());

    await act(async () =>
      server.heldReads[0].resolve([
        chatSummary("a", "Alpha"),
        chatSummary("b", "Beta", { hasUnread: true }),
      ]),
    );

    await waitFor(() => expect(rowDot("Beta")).not.toBeNull());
    expect(screen.queryByText("Alpha")).toBeNull();
    closePanel();
    expect(fabDot()).not.toBeNull();
  });
});
