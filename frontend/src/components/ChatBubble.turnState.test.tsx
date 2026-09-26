import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor, act } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import type { ChatMessage, ChatSession, ChatSessionSummary } from "../types";
import type { ChatHubEvents } from "../test-support";

// The turn the bubble believes is running outlives every request it makes: a
// send, a stop and a state read all resolve long after the click that started
// them, and each of them can land on a chat that has since moved on. These are
// the paths ChatBubble.test.tsx does not reach — a request that fails, and an
// answer that arrives for a chat the user has already left.
const {
  handlers,
  invoke,
  connection,
  chatService,
  aiProviderService,
  workItemService,
  getOpenLoopDocument,
} = vi.hoisted(() => ({
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
  workItemService: { listEditProposalsFor: vi.fn(() => Promise.resolve([])) },
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

vi.mock("../services/auth", () => ({ chatService, aiProviderService, workItemService }));
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

function emit<E extends keyof ChatHubEvents>(event: E, payload: ChatHubEvents[E]) {
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

  test("a send that fails after the user opened another chat reports nothing there", async () => {
    // The bubble is one component for every chat, so a request that outlives the
    // chat it belongs to must write nothing into whatever chat is open instead.
    openList(summary("s1", "First chat"), summary("s2", "Other chat"));
    chatService.getById.mockImplementation((id: string) =>
      Promise.resolve(chatSession({ id, name: id === "s1" ? "First chat" : "Other chat" })),
    );
    fireEvent.click(await screen.findByLabelText("Open chat"));
    fireEvent.click(await screen.findByText("First chat"));
    await screen.findByLabelText("Chat message");

    let failSend!: (err: Error) => void;
    chatService.sendMessage.mockReturnValue(
      new Promise<void>((_, reject) => {
        failSend = reject;
      }),
    );
    await sendMessageText("are you there?");

    // Leave for the other chat, and only then does the first send give up.
    fireEvent.click(screen.getByText("← Back"));
    fireEvent.click(await screen.findByText("Other chat"));
    await screen.findByLabelText("Chat message");
    await act(async () => {
      failSend(new Error("Network error."));
    });

    expect(screen.queryByText("Network error.")).toBeNull();
  });

  test("a stop still running in one chat leaves another chat's stop button usable", async () => {
    // The same rule for the flag that disables the button while a stop is in
    // flight: it belongs to the chat it was pressed in.
    openList(summary("s1", "First chat"), summary("s2", "Other chat"));
    chatService.getById.mockImplementation((id: string) =>
      Promise.resolve(
        chatSession({ id, name: id === "s1" ? "First chat" : "Other chat", activeTurnId: "t1" }),
      ),
    );
    chatService.interrupt.mockReturnValue(new Promise<void>(() => {}));

    fireEvent.click(await screen.findByLabelText("Open chat"));
    fireEvent.click(await screen.findByText("First chat"));
    await screen.findByLabelText("Chat message");
    fireEvent.click(await screen.findByLabelText("Stop"));
    await waitFor(() =>
      expect((screen.getByLabelText("Stop") as HTMLButtonElement).disabled).toBe(true),
    );

    // That stop never comes back. The other chat is running its own turn, and its
    // button has to work.
    fireEvent.click(screen.getByText("← Back"));
    fireEvent.click(await screen.findByText("Other chat"));
    await screen.findByLabelText("Chat message");

    const stop = await screen.findByLabelText("Stop");
    expect((stop as HTMLButtonElement).disabled).toBe(false);
  });

  test("a send that never comes back holds nothing back once the user has left the chat", async () => {
    // A claim belongs to the view that made it. This send's request never answers,
    // so it never releases its own claim, and leaving the chat is what drops it —
    // otherwise every read taken on the next visit would be discarded as "a send of
    // this chat's is still out", including the one that clears the controls after
    // the turn ends. That is a stop button and a working indicator over an idle
    // chat with nothing left to take them down.
    const view = openList(summary("s1", "First chat"));
    chatService.getById.mockResolvedValue(chatSession({ name: "First chat" }));
    fireEvent.click(await screen.findByLabelText("Open chat"));
    fireEvent.click(await screen.findByText("First chat"));
    await screen.findByLabelText("Chat message");

    chatService.sendMessage.mockReturnValue(new Promise<void>(() => {}));
    await sendMessageText("are you there?");
    expect(screen.getByLabelText("Stop")).toBeTruthy();

    // Away, and back to the same chat — which the server says is mid-turn.
    fireEvent.click(screen.getByText("← Back"));
    chatService.getById.mockResolvedValue(chatSession({ name: "First chat", activeTurnId: "t7" }));
    fireEvent.click(await screen.findByText("First chat"));
    await screen.findByLabelText("Chat message");
    expect(await screen.findByLabelText("Stop")).toBeTruthy();

    // The turn ends while the connection is down, so the completion never arrives
    // and the rejoin's read is the only thing that can say so. It has to be heard.
    chatService.getById.mockResolvedValue(chatSession({ name: "First chat", activeTurnId: null }));
    setConnectionState(view, "reconnecting");
    setConnectionState(view, "connected");
    await act(async () => {});

    await waitFor(() => expect(screen.queryByLabelText("Stop")).toBeNull());
    expect(screen.queryByRole("status")).toBeNull();
  });

  test("a stop that never comes back leaves that chat's button usable on the next visit", async () => {
    // The same rule for the other claim: the button is disabled while a stop is in
    // flight, and a stop that never answers would otherwise leave it disabled for
    // good — the ability to stop the chat lost again, which is the bug this work
    // item is about.
    openList(summary("s1", "First chat"));
    chatService.getById.mockResolvedValue(chatSession({ name: "First chat", activeTurnId: "t1" }));
    chatService.interrupt.mockReturnValue(new Promise<void>(() => {}));

    fireEvent.click(await screen.findByLabelText("Open chat"));
    fireEvent.click(await screen.findByText("First chat"));
    await screen.findByLabelText("Chat message");
    fireEvent.click(await screen.findByLabelText("Stop"));
    await waitFor(() =>
      expect((screen.getByLabelText("Stop") as HTMLButtonElement).disabled).toBe(true),
    );

    fireEvent.click(screen.getByText("← Back"));
    fireEvent.click(await screen.findByText("First chat"));
    await screen.findByLabelText("Chat message");

    const stop = await screen.findByLabelText("Stop");
    expect((stop as HTMLButtonElement).disabled).toBe(false);
  });

  test("a stop coming back late does not re-enable the button over a newer stop", async () => {
    // Letting go of the claim on leaving the chat makes two stops in one chat
    // reachable, and then the chat id alone no longer says which stop is which: the
    // first one to come back would release the claim of the one still in flight and
    // re-enable its button, ready to fire another interrupt. Each stop is numbered,
    // so only its own claim is the one it releases.
    openList(summary("s1", "First chat"));
    chatService.getById.mockResolvedValue(chatSession({ name: "First chat", activeTurnId: "t1" }));

    let finishFirstStop!: () => void;
    chatService.interrupt.mockReturnValueOnce(
      new Promise<void>((resolve) => {
        finishFirstStop = resolve;
      }),
    );

    fireEvent.click(await screen.findByLabelText("Open chat"));
    fireEvent.click(await screen.findByText("First chat"));
    await screen.findByLabelText("Chat message");
    fireEvent.click(await screen.findByLabelText("Stop"));
    await waitFor(() =>
      expect((screen.getByLabelText("Stop") as HTMLButtonElement).disabled).toBe(true),
    );

    // Away and back, which drops the claim of the stop still in flight, and the
    // button is live again — that much is intended.
    fireEvent.click(screen.getByText("← Back"));
    fireEvent.click(await screen.findByText("First chat"));
    await screen.findByLabelText("Chat message");
    expect((await screen.findByLabelText("Stop")).hasAttribute("disabled")).toBe(false);

    // The second stop, which the server has not answered either.
    chatService.interrupt.mockReturnValue(new Promise<void>(() => {}));
    fireEvent.click(screen.getByLabelText("Stop"));
    await waitFor(() =>
      expect((screen.getByLabelText("Stop") as HTMLButtonElement).disabled).toBe(true),
    );

    // Now the first one finally answers. The chat it names is this one, but the stop
    // it claimed is not the one holding the button.
    await act(async () => {
      finishFirstStop();
    });
    await act(async () => {});

    const stopButton = screen.getByLabelText("Stop") as HTMLButtonElement;
    expect(stopButton.disabled).toBe(true);
    expect(screen.getByRole("status")).toBeTruthy();
  });

  test("a replacement turn keeps its text when the displaced turn finalizes first", async () => {
    // The order the server produces: the replacement is announced, the turn it
    // interrupts finalizes, and only then does the replacement stream. Even with
    // the start announcement dropped — the client is left holding a placeholder —
    // the late reply belongs to the transcript and the new turn's text stands.
    await openResumed(chatSession({ activeTurnId: "t1" }));
    emit("ChatTurnProgress", { chatSessionId: "s1", turnId: "t1", delta: "half an answer" });

    chatService.sendMessage.mockResolvedValue(undefined);
    chatService.getById.mockResolvedValue(chatSession({ activeTurnId: "t2" }));
    await sendMessageText("stop that");

    emit("ChatMessageAppended", {
      chatSessionId: "s1",
      turnId: "t1",
      message: { ...msg({ id: "m1", content: "half an answer", sequence: 1 }), interrupted: true },
    });
    emit("ChatTurnProgress", { chatSessionId: "s1", turnId: "t2", delta: "a fresh reply" });

    expect(screen.getByText("interrupted")).toBeTruthy();
    expect(document.querySelector(".chat-msg-streaming")?.textContent).toBe("a fresh reply");
    expect(screen.getByLabelText("Stop")).toBeTruthy();
  });

  const FAILED_SEND_CASES: Array<{
    name: string;
    resumedTurn: string | null;
    streamed: string | null;
    read: string | null;
    readFails?: boolean;
    ends: "working" | "idle";
  }> = [
    {
      // The POST failed on the way back: the runner already has the message and is
      // working on it, so taking the stop button away would strand a live turn.
      name: "a send that fails asks the server rather than declaring the chat idle",
      resumedTurn: null,
      streamed: null,
      read: "t9",
      ends: "working",
    },
    {
      // The chat was idle when the send went out, so there is no earlier turn to
      // put back and the view is right to end up idle.
      name: "a send that fails with no answer from the server clears the turn it put up",
      resumedTurn: null,
      streamed: null,
      read: null,
      readFails: true,
      ends: "idle",
    },
    {
      // A send only displaces a turn if it arrives. This one fails on the way out,
      // so the turn it claimed to replace is still running and still streaming —
      // the reply on screen belongs to it and must not be wiped on its behalf.
      name: "a send that never reached the server leaves the turn it did not replace alone",
      resumedTurn: "t1",
      streamed: "half an answer",
      read: "t1",
      ends: "working",
    },
    {
      // Both requests fail, so nothing can confirm anything: the view must fall
      // back to what it knew, which is that t1 was running — not to idle, which
      // would take away the only control that can stop it.
      name: "a send that never reached the server keeps the stop button when the read fails too",
      resumedTurn: "t1",
      streamed: null,
      read: null,
      readFails: true,
      ends: "working",
    },
  ];

  test.each(FAILED_SEND_CASES)(
    "$name",
    async ({ resumedTurn, streamed, read, readFails, ends }) => {
      await openResumed(chatSession({ activeTurnId: resumedTurn }));
      if (streamed) {
        emit("ChatTurnProgress", { chatSessionId: "s1", turnId: "t1", delta: streamed });
        expect(screen.getByText(streamed)).toBeTruthy();
      }

      chatService.sendMessage.mockRejectedValue(new Error("Network error."));
      if (readFails) chatService.getById.mockRejectedValue(new Error("Network error."));
      else chatService.getById.mockResolvedValue(chatSession({ activeTurnId: read }));

      await sendMessageText("did this arrive?");

      expect(await screen.findByText("Network error.")).toBeTruthy();
      await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(3));
      if (ends === "working") {
        await waitFor(() => expect(screen.getByLabelText("Stop")).toBeTruthy());
        expect(screen.getByRole("status")).toBeTruthy();
      } else {
        await waitFor(() => expect(screen.queryByLabelText("Stop")).toBeNull());
        expect(screen.queryByRole("status")).toBeNull();
      }
      if (streamed) expect(screen.getByText(streamed)).toBeTruthy();
    },
  );

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
    // know about it. It carries a turn id like every append does, even though this
    // chat is idle and the id matches nothing the client is watching: the message
    // is transcript either way, and that is what this test is about.
    emit("ChatMessageAppended", {
      chatSessionId: "s1",
      turnId: "t6",
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

  // Sending a message answers with the turn it started, so the view knows its turn
  // without waiting to be told over the hub. These cover what that answer may and
  // may not do — including the case it exists for, where the start broadcast never
  // arrives at all.
  describe("a send's own answer", () => {
    /** A send whose request is held open, to be answered with `turnId` or refused. */
    function heldSendAnswering(turnId: string) {
      let answer!: (id: string) => void;
      chatService.sendMessage.mockReturnValue(
        new Promise<string>((resolve) => {
          answer = resolve;
        }),
      );
      return () => answer(turnId);
    }

    test("the answer names the turn even when the start broadcast never arrives", async () => {
      // The reason for it. With no start for the replacement, the view used to hold a
      // placeholder that matched anything, so the displaced turn — still finalizing —
      // could stream into the replacement's reply and then clear it. Nothing here
      // emits ChatTurnStarted at all, and the reconciling read fails too, so the
      // send's own answer is the only thing that can name the turn.
      await openResumed(chatSession({ activeTurnId: "t1" }));
      emit("ChatTurnProgress", { chatSessionId: "s1", turnId: "t1", delta: "half an answer" });
      expect(screen.getByText("half an answer")).toBeTruthy();

      const answerSend = heldSendAnswering("t2");
      chatService.getById.mockRejectedValue(new Error("Network error."));
      await sendMessageText("stop that");

      // Still the displaced turn's text on screen, and still its turn producing it.
      emit("ChatTurnProgress", { chatSessionId: "s1", turnId: "t1", delta: " continuing" });
      expect(screen.getByText("half an answer continuing")).toBeTruthy();

      await act(async () => {
        answerSend();
      });

      // The replacement is named, so what the displaced turn streamed is gone and
      // nothing more of it is taken.
      expect(document.querySelector(".chat-msg-streaming")).toBeNull();
      emit("ChatTurnProgress", { chatSessionId: "s1", turnId: "t1", delta: "STALE" });
      expect(document.querySelector(".chat-panel")?.textContent).not.toContain("STALE");

      // The replacement's own text is taken, before any start for it has arrived.
      emit("ChatTurnProgress", { chatSessionId: "s1", turnId: "t2", delta: "a fresh reply" });
      expect(document.querySelector(".chat-msg-streaming")?.textContent).toBe("a fresh reply");

      // And the displaced turn's finalized reply is transcript only: it must not
      // clear the reply now streaming.
      emit("ChatMessageAppended", {
        chatSessionId: "s1",
        turnId: "t1",
        message: {
          ...msg({ id: "m1", content: "half an answer", sequence: 1 }),
          interrupted: true,
        },
      });
      expect(document.querySelector(".chat-msg-streaming")?.textContent).toBe("a fresh reply");
      expect(screen.getByLabelText("Stop")).toBeTruthy();

      // The turn it named is the one that ends the chat.
      emit("ChatTurnCompleted", { chatSessionId: "s1", turnId: "t2", interrupted: false });
      expect(screen.queryByLabelText("Stop")).toBeNull();
    });

    test("an answer for a turn already announced changes nothing", async () => {
      await openResumed(chatSession({ activeTurnId: null }));
      const answerSend = heldSendAnswering("t2");
      chatService.getById.mockResolvedValue(chatSession({ activeTurnId: "t2" }));
      await sendMessageText("go");

      emit("ChatTurnStarted", { chatSessionId: "s1", turnId: "t2" });
      emit("ChatTurnProgress", { chatSessionId: "s1", turnId: "t2", delta: "already writing" });
      await act(async () => {
        answerSend();
      });

      // The same turn by another route: its streamed text has to survive.
      expect(document.querySelector(".chat-msg-streaming")?.textContent).toBe("already writing");
      expect(screen.getByLabelText("Stop")).toBeTruthy();
    });

    test("an answer for a turn that has already ended does not revive it", async () => {
      await openResumed(chatSession({ activeTurnId: null }));
      const answerSend = heldSendAnswering("t2");
      await sendMessageText("go");

      // The whole turn happens while the request is still in flight.
      emit("ChatTurnStarted", { chatSessionId: "s1", turnId: "t2" });
      emit("ChatTurnCompleted", { chatSessionId: "s1", turnId: "t2", interrupted: false });
      expect(screen.queryByLabelText("Stop")).toBeNull();

      chatService.getById.mockResolvedValue(chatSession({ activeTurnId: null }));
      await act(async () => {
        answerSend();
      });
      await act(async () => {});

      // A stop button for a turn that is over is the bug this work item is about.
      expect(screen.queryByLabelText("Stop")).toBeNull();
      expect(screen.queryByRole("status")).toBeNull();
    });

    const LATE_ANSWER_CASES: Array<{ name: string; chats: string[]; returnTo: string }> = [
      {
        name: "an answer never lands on a chat the user switched to",
        chats: ["First chat", "Other chat"],
        returnTo: "Other chat",
      },
      {
        name: "an answer never lands on a later visit to the same chat",
        chats: ["First chat"],
        returnTo: "First chat",
      },
    ];

    test.each(LATE_ANSWER_CASES)("$name", async ({ chats, returnTo }) => {
      openList(...chats.map((name, i) => summary(`s${i + 1}`, name)));
      chatService.getById.mockImplementation((id: string) =>
        Promise.resolve(chatSession({ id, name: id === "s1" ? "First chat" : "Other chat" })),
      );
      fireEvent.click(await screen.findByLabelText("Open chat"));
      fireEvent.click(await screen.findByText("First chat"));
      await screen.findByLabelText("Chat message");

      const answerSend = heldSendAnswering("t2");
      await sendMessageText("go");

      fireEvent.click(screen.getByText("← Back"));
      fireEvent.click(await screen.findByText(returnTo));
      await screen.findByLabelText("Chat message");

      await act(async () => {
        answerSend();
      });
      await act(async () => {});

      expect(screen.queryByLabelText("Stop")).toBeNull();
      expect(screen.queryByRole("status")).toBeNull();
    });
  });

  // Leaving a chat and opening it again is a second visit to the same chat id, so
  // the chat id alone cannot tell a request of the first visit's from the second's.
  // What separates them is the epoch, which moves on leaving a chat and again on
  // opening one, and the number each send and stop carries. These three cover the
  // case the chat-id checks above cannot: a result from the first visit arriving
  // during the second.
  describe("a result from an earlier visit to the same chat", () => {
    /** Opens "First chat" from the list, with whatever the server says about it. */
    async function openFirstChat() {
      fireEvent.click(await screen.findByText("First chat"));
      await screen.findByLabelText("Chat message");
    }

    test("a state read from the earlier visit never lands on the later one", async () => {
      const view = openList(summary("s1", "First chat"));
      chatService.getById.mockResolvedValue(
        chatSession({ name: "First chat", activeTurnId: "t1" }),
      );
      fireEvent.click(await screen.findByLabelText("Open chat"));
      await openFirstChat();

      // A rejoin read taken in the first visit, left unanswered.
      let answerFirstVisitRead!: (session: ChatSession) => void;
      chatService.getById.mockReturnValueOnce(
        new Promise<ChatSession>((resolve) => {
          answerFirstVisitRead = resolve;
        }),
      );
      setConnectionState(view, "reconnecting");
      setConnectionState(view, "connected");
      await waitFor(() => expect(chatService.getById).toHaveBeenCalledTimes(3));

      // Away and back into the same chat, which is running a different turn now.
      fireEvent.click(screen.getByText("← Back"));
      chatService.getById.mockResolvedValue(
        chatSession({ name: "First chat", activeTurnId: "t2" }),
      );
      await openFirstChat();
      expect(await screen.findByLabelText("Stop")).toBeTruthy();

      // The first visit's read answers at last, saying the chat was idle. It is
      // answering about a visit that is over, so it may not take the controls off
      // the turn this visit is watching.
      await act(async () => {
        answerFirstVisitRead(chatSession({ name: "First chat", activeTurnId: null }));
      });

      expect(screen.getByLabelText("Stop")).toBeTruthy();
      expect(screen.getByRole("status")).toBeTruthy();
    });

    test("a send failing in the earlier visit does not disturb the later one", async () => {
      openList(summary("s1", "First chat"));
      chatService.getById.mockResolvedValue(chatSession({ name: "First chat" }));
      fireEvent.click(await screen.findByLabelText("Open chat"));
      await openFirstChat();

      let failSend!: (err: Error) => void;
      chatService.sendMessage.mockReturnValue(
        new Promise<void>((_, reject) => {
          failSend = reject;
        }),
      );
      await sendMessageText("did this arrive?");
      expect(screen.getByLabelText("Stop")).toBeTruthy();

      // Away and back, and this visit has a turn of its own running.
      fireEvent.click(screen.getByText("← Back"));
      chatService.getById.mockResolvedValue(
        chatSession({ name: "First chat", activeTurnId: "t5" }),
      );
      await openFirstChat();
      expect(await screen.findByLabelText("Stop")).toBeTruthy();

      // The earlier visit's send fails now, while this visit's turn is still
      // running: the turn that send had put up must not be restored over it, and
      // the reconciliation read it makes can only report what the server says now.
      await act(async () => {
        failSend(new Error("Network error."));
      });
      await act(async () => {});

      expect(screen.getByLabelText("Stop")).toBeTruthy();
      expect(screen.getByRole("status")).toBeTruthy();
      // Documented, not defended: the error message does belong to this chat, and
      // it does surface on the next visit to it. Left as it is by decision.
      expect(screen.getByText("Network error.")).toBeTruthy();
    });

    test("a chat the user stopped opening never installs itself over the one they did", async () => {
      // Opening a chat is an awaited read, and the list stays on screen while it is
      // in flight, so two opens can be racing: click one chat, then another. The
      // slower answer belongs to a visit that never happened, and installing it
      // would put the user in a chat they did not open — with a stop button that
      // interrupts that chat's turn rather than anything they were looking at.
      openList(summary("s1", "First chat"), summary("s2", "Other chat"));
      let answerFirstOpen!: (session: ChatSession) => void;
      chatService.getById.mockImplementation((id: string) =>
        id === "s1"
          ? new Promise<ChatSession>((resolve) => {
              answerFirstOpen = resolve;
            })
          : Promise.resolve(
              chatSession({
                id: "s2",
                name: "Other chat",
                messages: [msg({ id: "b2", content: "the chat they opened", sequence: 0 })],
              }),
            ),
      );

      fireEvent.click(await screen.findByLabelText("Open chat"));
      fireEvent.click(await screen.findByText("First chat"));
      // Still on the list, because that read has not answered.
      fireEvent.click(await screen.findByText("Other chat"));
      await screen.findByLabelText("Chat message");
      expect(screen.getByText("the chat they opened")).toBeTruthy();

      await act(async () => {
        answerFirstOpen(
          chatSession({
            id: "s1",
            name: "First chat",
            activeTurnId: "t1",
            messages: [msg({ id: "b1", content: "the chat they left behind", sequence: 0 })],
          }),
        );
      });

      expect(screen.getByText("the chat they opened")).toBeTruthy();
      expect(screen.queryByText("the chat they left behind")).toBeNull();
      // And no controls from that chat's running turn on a view that is idle.
      expect(screen.queryByLabelText("Stop")).toBeNull();
      expect(screen.queryByRole("status")).toBeNull();
    });

    test("a read from the earlier visit cannot settle a send this visit is waiting on", async () => {
      // The sharper half of the same hazard: the stale read carries the *send
      // number* of the visit that made it, and that number is what lets a read
      // through the pending-send claim. It must not open this visit's claim.
      openList(summary("s1", "First chat"));
      chatService.getById.mockResolvedValue(chatSession({ name: "First chat" }));
      fireEvent.click(await screen.findByLabelText("Open chat"));
      await openFirstChat();

      let failFirstVisitSend!: (err: Error) => void;
      chatService.sendMessage.mockReturnValue(
        new Promise<void>((_, reject) => {
          failFirstVisitSend = reject;
        }),
      );
      await sendMessageText("first visit");

      fireEvent.click(screen.getByText("← Back"));
      await openFirstChat();

      // This visit posts its own message, and that request has not come back — so
      // the server may not have registered its turn yet, and only this send's own
      // read may settle what it put up.
      chatService.sendMessage.mockReturnValue(new Promise<void>(() => {}));
      await sendMessageText("this visit");
      expect(screen.getByLabelText("Stop")).toBeTruthy();

      // The earlier visit's send now fails and reads the chat, which the server
      // still calls idle because this visit's message has not landed.
      chatService.getById.mockResolvedValue(
        chatSession({ name: "First chat", activeTurnId: null }),
      );
      await act(async () => {
        failFirstVisitSend(new Error("Network error."));
      });
      await act(async () => {});

      expect(screen.getByLabelText("Stop")).toBeTruthy();
      expect(screen.getByRole("status")).toBeTruthy();
    });

    test("a stop settling from the earlier visit does not disturb the later one", async () => {
      openList(summary("s1", "First chat"));
      chatService.getById.mockResolvedValue(
        chatSession({ name: "First chat", activeTurnId: "t1" }),
      );
      let finishFirstVisitStop!: () => void;
      chatService.interrupt.mockReturnValueOnce(
        new Promise<void>((resolve) => {
          finishFirstVisitStop = resolve;
        }),
      );

      fireEvent.click(await screen.findByLabelText("Open chat"));
      await openFirstChat();
      fireEvent.click(await screen.findByLabelText("Stop"));
      await waitFor(() =>
        expect((screen.getByLabelText("Stop") as HTMLButtonElement).disabled).toBe(true),
      );

      // Away and back, then a stop of this visit's own, still in flight.
      fireEvent.click(screen.getByText("← Back"));
      await openFirstChat();
      chatService.interrupt.mockReturnValue(new Promise<void>(() => {}));
      fireEvent.click(await screen.findByLabelText("Stop"));
      await waitFor(() =>
        expect((screen.getByLabelText("Stop") as HTMLButtonElement).disabled).toBe(true),
      );

      // The earlier visit's stop settles. Its claim is gone with its visit, and the
      // one holding the button belongs to a different stop.
      await act(async () => {
        finishFirstVisitStop();
      });
      await act(async () => {});

      expect((screen.getByLabelText("Stop") as HTMLButtonElement).disabled).toBe(true);
      expect(screen.getByRole("status")).toBeTruthy();
    });
  });
});
