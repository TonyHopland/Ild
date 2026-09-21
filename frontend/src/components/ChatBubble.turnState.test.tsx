import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor, act } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import type { ChatMessage, ChatSession, ChatSessionSummary } from "../types";

// The turn the bubble believes is running outlives every request it makes: a
// send, a stop and a state read all resolve long after the click that started
// them, and each of them can land on a chat that has since moved on. These are
// the paths ChatBubble.test.tsx does not reach — a request that fails, and an
// answer that arrives for a chat the user has already left.
const { handlers, invoke, connection, chatService, aiProviderService, getOpenLoopDocument } =
  vi.hoisted(() => ({
    handlers: {} as Record<string, (msg: { payload: unknown }) => void>,
    invoke: vi.fn(() => Promise.resolve()),
    // The bubble only reads the connection state while rendering, so a test
    // drops and restores it by setting this and re-rendering.
    connection: { state: "connected" as string },
    chatService: {
      listHistory: vi.fn(),
      getById: vi.fn(),
      start: vi.fn(),
      sendMessage: vi.fn(),
      interrupt: vi.fn(),
      deleteOne: vi.fn(),
      deleteAll: vi.fn(),
    },
    aiProviderService: { getAll: vi.fn() },
    getOpenLoopDocument: vi.fn(),
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

vi.mock("../services/auth", () => ({ chatService, aiProviderService }));
vi.mock("../utils/openLoopDocument", () => ({ getOpenLoopDocument }));
vi.mock("../services/chatSessionStore", () => ({ setCurrentChatSessionId: vi.fn() }));

import ChatBubble from "./ChatBubble";

function chatSession(partial: Partial<ChatSession> = {}): ChatSession {
  return {
    id: partial.id ?? "s1",
    name: partial.name ?? "Past chat",
    aiProviderId: "p1",
    providerType: "claude-code",
    tools: ["ild"],
    createdAt: "2026-01-01T00:00:00Z",
    messages: partial.messages ?? [],
    activeTurnId: partial.activeTurnId ?? null,
  };
}

function msg(partial: Partial<ChatMessage>): ChatMessage {
  return {
    id: partial.id ?? crypto.randomUUID(),
    role: partial.role ?? "assistant",
    content: partial.content ?? "",
    interrupted: false,
    sequence: partial.sequence ?? 0,
    createdAt: "2026-01-01T00:00:00Z",
  };
}

function summary(id: string, name: string): ChatSessionSummary {
  return { id, name, createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-02T00:00:00Z" };
}

function emit(event: string, payload: Record<string, unknown>) {
  act(() => {
    handlers[event]?.({ payload });
  });
}

/** Open the bubble on the chat list, with the given chats in the history. */
function openList(...chats: ChatSessionSummary[]) {
  chatService.listHistory.mockResolvedValue(chats);
  // The start form loads them behind the list; none of these tests start a chat.
  aiProviderService.getAll.mockResolvedValue([]);
  return render(
    <MemoryRouter initialEntries={["/"]}>
      <ChatBubble />
    </MemoryRouter>,
  );
}

/**
 * Resume a chat and wait for the join's own state read, so a test that swaps the
 * `getById` mock afterwards is swapping it for its own call and not for that one.
 */
async function openResumed(session: ChatSession) {
  const view = openList(summary(session.id, session.name ?? "Past chat"));
  chatService.getById.mockResolvedValue(session);
  fireEvent.click(await screen.findByLabelText("Open chat"));
  fireEvent.click(await screen.findByText(session.name ?? "Past chat"));
  await screen.findByLabelText("Chat message");
  await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(2));
  return view;
}

/** Drop or restore the live connection; the bubble reads it while rendering. */
function setConnectionState(view: ReturnType<typeof openList>, state: string) {
  connection.state = state;
  act(() => {
    view.rerender(
      <MemoryRouter initialEntries={["/"]}>
        <ChatBubble />
      </MemoryRouter>,
    );
  });
}

async function sendMessageText(text: string) {
  const input = await screen.findByLabelText("Chat message");
  fireEvent.change(input, { target: { value: text } });
  fireEvent.click(screen.getByText("Send"));
}

afterEach(() => {
  cleanup();
  for (const k of Object.keys(handlers)) delete handlers[k];
  vi.clearAllMocks();
  getOpenLoopDocument.mockReset();
  connection.state = "connected";
  localStorage.clear();
});

describe("ChatBubble turn state", () => {
  test("a snapshot taken while a send is in flight cannot take the controls away", async () => {
    // A rejoin reads the chat between the send going out and the server
    // registering its turn, so it answers idle — truthfully, and already out of
    // date. Only the send's own read, taken once the request has come back, knows
    // whether the turn was accepted.
    const view = await openResumed(chatSession());

    let acceptSend!: () => void;
    chatService.sendMessage.mockReturnValue(
      new Promise<void>((resolve) => {
        acceptSend = resolve;
      }),
    );
    await sendMessageText("are you there?");
    expect(screen.getByLabelText("Stop")).toBeTruthy();

    // The rejoin lands first, with the chat still idle as far as the server knows.
    chatService.getById.mockResolvedValue(chatSession({ activeTurnId: null }));
    setConnectionState(view, "reconnecting");
    setConnectionState(view, "connected");
    await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(3));
    await act(async () => {});

    expect(screen.getByLabelText("Stop")).toBeTruthy();
    expect(screen.getByRole("status")).toBeTruthy();

    // Then the send is accepted, and its own read names the turn it started.
    chatService.getById.mockResolvedValue(chatSession({ activeTurnId: "t4" }));
    await act(async () => {
      acceptSend();
    });
    await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(4));

    expect(screen.getByLabelText("Stop")).toBeTruthy();
    emit("ChatTurnCompleted", { chatSessionId: "s1", turnId: "t4", interrupted: false });
    expect(screen.queryByLabelText("Stop")).toBeNull();
  });

  test("a send waiting in one chat holds nothing back in another", async () => {
    // The pending state belongs to the chat it was sent to. Another chat reads its
    // own state as usual — otherwise opening one while a send hangs in the other
    // leaves it showing whatever it happened to be showing, right or wrong.
    chatService.interrupt.mockResolvedValue(undefined);
    const view = openList(summary("s1", "First chat"), summary("s2", "Other chat"));
    chatService.getById.mockImplementation((id: string) =>
      Promise.resolve(
        id === "s1"
          ? chatSession({ id: "s1", name: "First chat" })
          : chatSession({ id: "s2", name: "Other chat", activeTurnId: "t9" }),
      ),
    );

    fireEvent.click(await screen.findByLabelText("Open chat"));
    fireEvent.click(await screen.findByText("First chat"));
    await screen.findByLabelText("Chat message");

    // A send to the first chat that never comes back.
    chatService.sendMessage.mockReturnValue(new Promise<void>(() => {}));
    await sendMessageText("are you there?");
    expect(screen.getByLabelText("Stop")).toBeTruthy();

    // Leave it hanging and open the other chat, whose turn is running.
    fireEvent.click(screen.getByText("← Back"));
    fireEvent.click(await screen.findByText("Other chat"));
    await screen.findByLabelText("Chat message");
    await waitFor(() => expect(screen.getByLabelText("Stop")).toBeTruthy());

    // That turn ends while we are watching it, and the rejoin must be able to say
    // so — the other chat's pending send has no bearing on this one.
    chatService.getById.mockResolvedValue(chatSession({ id: "s2", name: "Other chat" }));
    setConnectionState(view, "reconnecting");
    setConnectionState(view, "connected");
    await act(async () => {});

    await waitFor(() => expect(screen.queryByLabelText("Stop")).toBeNull());
    expect(screen.queryByRole("status")).toBeNull();
  });

  test("a send rejected after an idle snapshot settles the chat as idle", async () => {
    // The same race, the other outcome: the send never reached the server, so
    // once it comes back there is nothing running and the controls go.
    const view = await openResumed(chatSession());

    let rejectSend!: (err: Error) => void;
    chatService.sendMessage.mockReturnValue(
      new Promise<void>((_, reject) => {
        rejectSend = reject;
      }),
    );
    await sendMessageText("are you there?");

    chatService.getById.mockResolvedValue(chatSession({ activeTurnId: null }));
    setConnectionState(view, "reconnecting");
    setConnectionState(view, "connected");
    await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(3));
    await act(async () => {});
    expect(screen.getByLabelText("Stop")).toBeTruthy();

    await act(async () => {
      rejectSend(new Error("Network error."));
    });
    await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(4));

    expect(await screen.findByText("Network error.")).toBeTruthy();
    await waitFor(() => expect(screen.queryByLabelText("Stop")).toBeNull());
    expect(screen.queryByRole("status")).toBeNull();
  });

  test("a send that fails asks the server rather than declaring the chat idle", async () => {
    await openResumed(chatSession());
    chatService.sendMessage.mockRejectedValue(new Error("Network error."));
    // The POST failed on the way back: the runner already has the message and is
    // working on it, so taking the stop button away would strand a live turn.
    chatService.getById.mockResolvedValue(chatSession({ activeTurnId: "t9" }));

    await sendMessageText("did this arrive?");

    expect(await screen.findByText("Network error.")).toBeTruthy();
    await waitFor(() => expect(screen.getByLabelText("Stop")).toBeTruthy());
    expect(screen.getByRole("status")).toBeTruthy();
  });

  test("a send that fails with no answer from the server clears the turn it put up", async () => {
    // The chat was idle when the send went out, so there is no earlier turn to
    // put back and the view is right to end up idle.
    await openResumed(chatSession());
    chatService.sendMessage.mockRejectedValue(new Error("Network error."));
    chatService.getById.mockRejectedValue(new Error("Network error."));

    await sendMessageText("did this arrive?");

    expect(await screen.findByText("Network error.")).toBeTruthy();
    await waitFor(() => expect(screen.queryByLabelText("Stop")).toBeNull());
    expect(screen.queryByRole("status")).toBeNull();
  });

  test("a send that never reached the server leaves the turn it did not replace alone", async () => {
    // A send only displaces a turn if it arrives. This one fails on the way out,
    // so the turn it claimed to replace is still running and still streaming —
    // the reply on screen belongs to it and must not be wiped on its behalf.
    await openResumed(chatSession({ activeTurnId: "t1" }));
    emit("ChatTurnProgress", { chatSessionId: "s1", turnId: "t1", delta: "half an answer" });
    expect(screen.getByText("half an answer")).toBeTruthy();

    chatService.sendMessage.mockRejectedValue(new Error("Network error."));
    chatService.getById.mockResolvedValue(chatSession({ activeTurnId: "t1" }));

    await sendMessageText("are you still there?");

    expect(await screen.findByText("Network error.")).toBeTruthy();
    await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(3));
    expect(screen.getByLabelText("Stop")).toBeTruthy();
    expect(screen.getByRole("status")).toBeTruthy();
    expect(screen.getByText("half an answer")).toBeTruthy();
  });

  test("a send that never reached the server keeps the stop button when the read fails too", async () => {
    // Both requests fail, so nothing can confirm anything: the view must fall
    // back to what it knew, which is that t1 was running — not to idle, which
    // would take away the only control that can stop it.
    await openResumed(chatSession({ activeTurnId: "t1" }));
    chatService.sendMessage.mockRejectedValue(new Error("Network error."));
    chatService.getById.mockRejectedValue(new Error("Network error."));

    await sendMessageText("are you still there?");

    expect(await screen.findByText("Network error.")).toBeTruthy();
    await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(3));
    expect(screen.getByLabelText("Stop")).toBeTruthy();
    expect(screen.getByRole("status")).toBeTruthy();
  });

  test("a replacement turn starts with no text from the turn it replaced", async () => {
    // The interrupted turn's finalized reply is the usual thing that clears the
    // streamed buffer, and it is exactly what an outage drops. Deltas append, so
    // a buffer carried over would grow the new turn's reply on top of the old.
    await openResumed(chatSession({ activeTurnId: "t1" }));
    emit("ChatTurnProgress", { chatSessionId: "s1", turnId: "t1", delta: "half an answer" });
    expect(screen.getByText("half an answer")).toBeTruthy();

    emit("ChatTurnStarted", { chatSessionId: "s1", turnId: "t2" });
    expect(document.querySelector(".chat-panel")?.textContent).not.toContain("half an answer");
    // Nothing has been streamed for this turn yet, so it is not responding yet.
    expect(screen.getByRole("status").textContent).toContain("Thinking");

    emit("ChatTurnProgress", { chatSessionId: "s1", turnId: "t2", delta: "a fresh reply" });
    expect(screen.getByText("a fresh reply")).toBeTruthy();
    expect(document.querySelector(".chat-msg-streaming")?.textContent).toBe("a fresh reply");
  });

  test("the interrupted reply arriving late is transcript, not the new turn's text", async () => {
    await openResumed(chatSession({ activeTurnId: "t1" }));
    emit("ChatTurnProgress", { chatSessionId: "s1", turnId: "t1", delta: "half an answer" });
    emit("ChatTurnStarted", { chatSessionId: "s1", turnId: "t2" });
    emit("ChatTurnProgress", { chatSessionId: "s1", turnId: "t2", delta: "a fresh reply" });
    expect(document.querySelector(".chat-msg-streaming")?.textContent).toBe("a fresh reply");

    // The interrupted turn's own finalized reply turns up after its replacement
    // had started. It belongs in the transcript, flagged interrupted — and it
    // belongs nowhere near what the live turn is writing: only that turn's own
    // reply may replace its streamed text.
    emit("ChatMessageAppended", {
      chatSessionId: "s1",
      turnId: "t1",
      message: {
        ...msg({ id: "m1", content: "half an answer", sequence: 1 }),
        interrupted: true,
      },
    });
    expect(screen.getAllByText("half an answer")).toHaveLength(1);
    expect(screen.getByText("interrupted")).toBeTruthy();
    expect(document.querySelector(".chat-msg-streaming")?.textContent).toBe("a fresh reply");

    // The live turn carries on from where it was, and its own finalized reply is
    // what finally replaces the text.
    emit("ChatTurnProgress", { chatSessionId: "s1", turnId: "t2", delta: " carrying on" });
    expect(document.querySelector(".chat-msg-streaming")?.textContent).toBe(
      "a fresh reply carrying on",
    );
    emit("ChatMessageAppended", {
      chatSessionId: "s1",
      turnId: "t2",
      message: msg({ id: "m2", content: "a fresh reply carrying on", sequence: 2 }),
    });
    expect(document.querySelector(".chat-msg-streaming")).toBeNull();
  });

  test("a turn announced while a failed send unwinds survives it", async () => {
    await openResumed(chatSession());

    let failSend!: (err: Error) => void;
    chatService.sendMessage.mockReturnValue(
      new Promise<void>((_, reject) => {
        failSend = reject;
      }),
    );
    let failRead!: (err: Error) => void;
    chatService.getById.mockReturnValue(
      new Promise<ChatSession>((_, reject) => {
        failRead = reject;
      }),
    );

    await sendMessageText("did this arrive?");
    await act(async () => {
      failSend(new Error("Network error."));
    });
    await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(3));

    // The runner did get the message: it announces the turn while the client is
    // still unwinding the send that failed.
    emit("ChatTurnStarted", { chatSessionId: "s1", turnId: "t5" });
    await act(async () => {
      failRead(new Error("Network error."));
    });

    expect(screen.getByLabelText("Stop")).toBeTruthy();
    expect(screen.getByRole("status")).toBeTruthy();

    // And it is that turn's completion that ends it, not the failed send's.
    emit("ChatTurnCompleted", { chatSessionId: "s1", turnId: "t5", interrupted: false });
    expect(screen.queryByLabelText("Stop")).toBeNull();
  });

  test("a long transcript is merged whole, keeping order and what arrived meanwhile", async () => {
    // The snapshot is a whole transcript rather than the single message an append
    // carries, and it is always behind by whatever landed while it was in flight.
    // Merging it may add what was missed and reorder, never drop or duplicate.
    const history = Array.from({ length: 60 }, (_, i) =>
      msg({ id: `m${i}`, content: `body ${i}`, sequence: i }),
    );
    openList(summary("s1", "Long chat"));
    chatService.getById.mockResolvedValueOnce(
      chatSession({ name: "Long chat", messages: history }),
    );
    let answerJoinRead!: (session: ChatSession) => void;
    chatService.getById.mockReturnValueOnce(
      new Promise<ChatSession>((resolve) => {
        answerJoinRead = resolve;
      }),
    );

    fireEvent.click(await screen.findByLabelText("Open chat"));
    fireEvent.click(await screen.findByText("Long chat"));
    await screen.findByLabelText("Chat message");

    // Learned over the hub while the read was in flight, so the snapshot cannot
    // know about it.
    emit("ChatMessageAppended", {
      chatSessionId: "s1",
      message: msg({ id: "live", content: "arrived meanwhile", sequence: 60 }),
    });

    // The server answers with the same transcript out of order, plus one the
    // client had not seen.
    await act(async () => {
      answerJoinRead(
        chatSession({
          name: "Long chat",
          messages: [msg({ id: "late", content: "body 61", sequence: 61 }), ...history].reverse(),
        }),
      );
    });

    const rendered = [...document.querySelectorAll(".chat-msg")].map((n) => n.textContent);
    expect(rendered).toHaveLength(history.length + 2);
    expect(rendered).toEqual([...history.map((m) => m.content), "arrived meanwhile", "body 61"]);
    expect(screen.getAllByText("body 5")).toHaveLength(1);
  });

  test("the newer of two overlapping state reads is the one that decides", async () => {
    // A join and a stop's own reconciliation can be in flight at once. Ordering a
    // read against turn changes is not enough: at the same epoch, whichever
    // answers first applies and moves the epoch, discarding the other — and when
    // the one discarded is the stop's newer answer, the chat keeps a stop button
    // for a turn the server has already ended.
    chatService.interrupt.mockResolvedValue(undefined);
    const view = await openResumed(chatSession({ activeTurnId: "t1" }));
    expect(screen.getByLabelText("Stop")).toBeTruthy();

    // The join's read goes out first and hangs: its snapshot still says running.
    let answerJoinRead!: (session: ChatSession) => void;
    chatService.getById.mockReturnValueOnce(
      new Promise<ChatSession>((resolve) => {
        answerJoinRead = resolve;
      }),
    );
    setConnectionState(view, "reconnecting");
    setConnectionState(view, "connected");
    await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(3));

    // Then the user stops the turn, and that read goes out second.
    let answerStopRead!: (session: ChatSession) => void;
    chatService.getById.mockReturnValueOnce(
      new Promise<ChatSession>((resolve) => {
        answerStopRead = resolve;
      }),
    );
    fireEvent.click(screen.getByLabelText("Stop"));
    await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(4));

    // The stale join answer lands first, then the truth.
    await act(async () => {
      answerJoinRead(chatSession({ activeTurnId: "t1" }));
    });
    await act(async () => {
      answerStopRead(chatSession({ activeTurnId: null }));
    });

    expect(screen.queryByLabelText("Stop")).toBeNull();
    expect(screen.queryByRole("status")).toBeNull();
  });

  test("a read that never answers takes nothing with it", async () => {
    // Overlapping reads are ordered by what they answer, not by what they ask:
    // a later read that fails must not silence an earlier one that succeeded,
    // or a stopped chat keeps a stop button for a turn that has ended.
    chatService.interrupt.mockResolvedValue(undefined);
    const view = await openResumed(chatSession({ activeTurnId: "t1" }));

    // The stop's own read goes out first and will answer: the turn is over.
    let answerStopRead!: (session: ChatSession) => void;
    chatService.getById.mockReturnValueOnce(
      new Promise<ChatSession>((resolve) => {
        answerStopRead = resolve;
      }),
    );
    fireEvent.click(screen.getByLabelText("Stop"));
    await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(3));

    // A rejoin's read goes out after it, and fails.
    let failJoinRead!: (err: Error) => void;
    chatService.getById.mockReturnValueOnce(
      new Promise<ChatSession>((_, reject) => {
        failJoinRead = reject;
      }),
    );
    setConnectionState(view, "reconnecting");
    setConnectionState(view, "connected");
    await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(4));

    await act(async () => {
      failJoinRead(new Error("Network error."));
    });
    await act(async () => {
      answerStopRead(chatSession({ activeTurnId: null }));
    });

    expect(screen.queryByLabelText("Stop")).toBeNull();
    expect(screen.queryByRole("status")).toBeNull();
  });

  test("a failed send's own read still lands when a later read fails", async () => {
    // The mirror image: the answer that survives says the chat is busy, and
    // losing it would take the stop button off a turn that is still running.
    const view = await openResumed(chatSession({ activeTurnId: null }));
    chatService.sendMessage.mockRejectedValue(new Error("Network error."));

    let answerSendRead!: (session: ChatSession) => void;
    chatService.getById.mockReturnValueOnce(
      new Promise<ChatSession>((resolve) => {
        answerSendRead = resolve;
      }),
    );
    await sendMessageText("did this arrive?");
    await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(3));

    let failJoinRead!: (err: Error) => void;
    chatService.getById.mockReturnValueOnce(
      new Promise<ChatSession>((_, reject) => {
        failJoinRead = reject;
      }),
    );
    setConnectionState(view, "reconnecting");
    setConnectionState(view, "connected");
    await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(4));

    await act(async () => {
      failJoinRead(new Error("Network error."));
    });
    await act(async () => {
      answerSendRead(chatSession({ activeTurnId: "t2" }));
    });

    expect(screen.getByLabelText("Stop")).toBeTruthy();
    expect(screen.getByRole("status")).toBeTruthy();
  });

  test("a state read answered after the user left never lands on the chat they moved to", async () => {
    chatService.interrupt.mockResolvedValue(undefined);
    chatService.sendMessage.mockResolvedValue(undefined);
    openList(summary("s1", "First chat"), summary("s2", "Other chat"));
    chatService.getById.mockResolvedValue(chatSession({ id: "s1", name: "First chat" }));

    fireEvent.click(await screen.findByLabelText("Open chat"));
    fireEvent.click(await screen.findByText("First chat"));
    await screen.findByLabelText("Chat message");

    await sendMessageText("long task");
    emit("ChatTurnStarted", { chatSessionId: "s1", turnId: "t1" });

    // The stop's own read hangs while the server is still cancelling.
    let answerFirst!: (session: ChatSession) => void;
    chatService.getById.mockImplementation((id: string) =>
      id === "s1"
        ? new Promise<ChatSession>((resolve) => {
            answerFirst = resolve;
          })
        : Promise.resolve(chatSession({ id: "s2", name: "Other chat" })),
    );

    fireEvent.click(await screen.findByLabelText("Stop"));
    await waitFor(() => expect(chatService.interrupt).toHaveBeenCalledWith("s1"));

    fireEvent.click(screen.getByText("← Back"));
    fireEvent.click(await screen.findByText("Other chat"));
    await screen.findByLabelText("Chat message");

    // The first chat finally answers, and it is still busy — but the bubble is
    // looking at another chat now, so neither its turn nor its transcript may
    // reach the one on screen.
    await act(async () => {
      answerFirst(
        chatSession({
          id: "s1",
          name: "First chat",
          activeTurnId: "t1",
          messages: [msg({ id: "m1", content: "belongs to the first chat", sequence: 0 })],
        }),
      );
    });

    expect(screen.queryByLabelText("Stop")).toBeNull();
    expect(screen.queryByRole("status")).toBeNull();
    expect(screen.queryByText("belongs to the first chat")).toBeNull();
  });
});
