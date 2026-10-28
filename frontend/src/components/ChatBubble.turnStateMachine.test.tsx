import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { screen, fireEvent, waitFor, act } from "@testing-library/react";
import type { ChatMessage, ChatSession } from "../types";
import {
  chatBubbleTree,
  chatMessageOf,
  chatSessionOf,
  chatSummary,
  emitChatEvent as emit,
  openChatFromList,
  openChatPanel,
  renderChatBubble,
  signalRHub,
  stubChatServices,
} from "../test-support";

// One table over the whole turn lifecycle. Every case is a sequence of the things
// that can happen to a chat — a send, the server announcing a turn, streamed text,
// a finalized message, a stop, a completion, a reconnect, a state read — and every
// case asserts the only two things this work item promises: a chat with work in
// flight shows the stop button and the working indicator, and an idle one shows
// neither. They are checked after every single step, not just at the end, because
// a view that is briefly wrong is exactly the bug being fixed.
//
// No single event is required for any of it: a lost start, a lost completion and a
// dropped connection all appear below, and the view still has to end up matching
// the server.
vi.mock("../hooks/useSignalR", async () => (await import("../test-support")).fakeSignalRModule());
vi.mock("../utils/openLoopDocument", () => ({ getOpenLoopDocument: vi.fn() }));
vi.mock("../services/chatSessionStore", () => ({ setCurrentChatSessionId: vi.fn() }));

const { chatService, aiProviderService } = stubChatServices();

const CHAT = "s1";

const chatSession = (activeTurnId: string | null, messages: ChatMessage[] = []) =>
  chatSessionOf({ id: CHAT, activeTurnId, messages });

const msg = (id: string, content: string, interrupted = false) =>
  chatMessageOf({ id, content, interrupted, sequence: Number(id.replace(/\D/g, "")) || 1 });

/** What the server would answer a state read with, until a step changes it. */
let serverTurn: string | null = null;
/** Whether the next state read answers straight away or hangs, to be answered late. */
let nextRead: "answers" | "hangs" = "answers";
/** The resolver of a read being held open, once one has been taken. */
let heldRead: ((session: ChatSession) => void) | null = null;
/** The send request left hanging, and what it will report when it settles. */
let heldSend: { accept: () => void; reject: (err: Error) => void; turn: string } | null = null;

type Step =
  /** The user sends a message. The server accepts it and reports `turn` from then on. */
  | { send: string }
  /** The server announces a turn over the hub. */
  | { start: string }
  /** A streamed delta from `turn`. */
  | { progress: string; text: string }
  /** A finalized message from `turn` — which may be one already replaced. */
  | { append: string; text: string; interrupted?: boolean }
  /** The server reports that `turn` has ended. */
  | { complete: string }
  /** Another chat's streamed text, finalized reply and completion, all under `turn`: none of it is this chat's. */
  | { elsewhere: string }
  /** The turn ends server-side and the client is never told — a dropped broadcast. */
  | { serverEnds: true }
  /** The user presses stop; the server reports the chat idle afterwards. */
  | { interrupt: true }
  /** The connection drops and comes back, so the bubble rejoins and re-reads. */
  | { reconnect: true }
  /** A send whose request hangs, so the chat is pending while other things happen. */
  | { sendPending: string }
  /** That send finally comes back, accepted by the server or refused. */
  | { settleSend: "accepted" | "rejected" }
  /** The next state read hangs, to be answered later and out of order. */
  | { holdRead: true }
  /** The held read finally answers, with what the server thought at the time. */
  | { answerHeldRead: string | null };

/** Any step may pin what the view must show the moment it has been played. */
type Expected = "working" | "idle";

/** What the working indicator says while the chat is working. */
type Reads = "Thinking" | "Responding";

interface Case {
  name: string;
  /** The turn the chat is running when it is opened, if any. */
  resumeWith?: string | null;
  steps: (Step & { shows?: Expected; reads?: Reads })[];
  /** What the view must show once every step has been played. */
  ends: "working" | "idle";
}

const CASES: Case[] = [
  {
    name: "a turn runs to completion",
    steps: [{ send: "t1" }, { start: "t1" }, { progress: "t1", text: "hi" }, { complete: "t1" }],
    ends: "idle",
  },
  {
    name: "a turn still streaming keeps the controls",
    steps: [{ send: "t1" }, { start: "t1" }, { progress: "t1", text: "hi" }],
    ends: "working",
  },
  {
    name: "the indicator reads Thinking until text streams, then Responding, until the turn ends",
    steps: [
      { send: "t1", reads: "Thinking" },
      { start: "t1", reads: "Thinking" },
      { progress: "t1", text: "half an ans", reads: "Responding" },
      { append: "t1", text: "half an answer", reads: "Thinking" },
      { complete: "t1", shows: "idle" },
    ],
    ends: "idle",
  },
  {
    name: "another chat's text, reply and completion never touch the working one",
    steps: [{ send: "t1" }, { start: "t1" }, { elsewhere: "t1", shows: "working" }],
    ends: "working",
  },
  {
    name: "a lost start is settled by the send's own read, and the turn still ends",
    // The start broadcast never arrives; only the read tells the client its name.
    steps: [{ send: "t1" }, { progress: "t1", text: "hi" }, { complete: "t1" }],
    ends: "idle",
  },
  {
    name: "a lost completion is settled by the next rejoin",
    steps: [{ send: "t1" }, { start: "t1" }, { serverEnds: true }, { reconnect: true }],
    ends: "idle",
  },
  {
    name: "an interrupting send keeps the controls across the hand-over",
    steps: [
      { send: "t1" },
      { start: "t1" },
      { progress: "t1", text: "half" },
      { send: "t2" },
      { start: "t2" },
      { progress: "t2", text: "fresh" },
      // The turn that was interrupted finalizes late; it is transcript, not state.
      { append: "t1", text: "half", interrupted: true },
    ],
    ends: "working",
  },
  {
    name: "the replacement turn ends the chat, not the turn it replaced",
    steps: [
      { send: "t1" },
      { start: "t1" },
      { send: "t2" },
      { start: "t2" },
      { complete: "t1" },
      { complete: "t2" },
    ],
    ends: "idle",
  },
  {
    name: "a stop ends the turn",
    steps: [{ send: "t1" }, { start: "t1" }, { interrupt: true }],
    ends: "idle",
  },
  {
    name: "a chat resumed mid-turn is working, and that turn can end it",
    resumeWith: "t9",
    steps: [{ complete: "t9" }],
    ends: "idle",
  },
  {
    name: "a chat resumed idle stays idle through another chat's traffic",
    steps: [{ start: "x1" }, { progress: "x1", text: "not mine" }, { complete: "x1" }],
    ends: "idle",
  },
  {
    name: "a reconnect mid-turn keeps the controls",
    steps: [{ send: "t1" }, { start: "t1" }, { reconnect: true }],
    ends: "working",
  },
  {
    name: "a reconnect while a send is in flight cannot take the controls away",
    // The rejoin reads the chat before the server has registered the turn, so it
    // answers idle — truthfully, and already out of date.
    steps: [
      { sendPending: "t1" },
      // The idle snapshot lands here and must change nothing.
      { reconnect: true, shows: "working" },
      { settleSend: "accepted" },
      { start: "t1" },
    ],
    ends: "working",
  },
  {
    name: "a send refused after that reconnect leaves the chat idle",
    steps: [
      { sendPending: "t1" },
      { reconnect: true, shows: "working" },
      { settleSend: "rejected" },
    ],
    ends: "idle",
  },
  {
    name: "a read answered out of order never overrules the newer turn",
    steps: [
      { send: "t1" },
      { start: "t1" },
      { holdRead: true },
      { reconnect: true },
      { send: "t2" },
      { start: "t2" },
      // Taken while t1 was still running, answered long after t2 began.
      { answerHeldRead: "t1" },
    ],
    ends: "working",
  },
  {
    name: "a read taken after a hand-over is announced reports the replacement",
    // Another client's send replaces t1, so this one has no pending send to discard
    // the read by; and the read is taken after the start of t2 arrived, so it is not
    // a snapshot from before a turn we have since learned about either. Both guards
    // that usually protect this client are out, and what the server answers is what
    // it applies — the replacement, because that is the turn the events from here on
    // are about. See ChatTurn.Current.
    resumeWith: "t1",
    steps: [
      { start: "t2", shows: "working" },
      { holdRead: true },
      { reconnect: true },
      { answerHeldRead: "t2", shows: "working" },
      // t1's completion is still to come, and cannot be taken for the chat ending.
      { complete: "t1", shows: "working" },
    ],
    ends: "working",
  },
  {
    name: "the same read answering with the turn being replaced is what would break it",
    // The server should answer with the replacement, but if it answers with the outgoing turn
    // the view still keeps t2, so t1's completion cannot take the controls away under it.
    resumeWith: "t1",
    steps: [
      { start: "t2", shows: "working" },
      { holdRead: true },
      { reconnect: true },
      { answerHeldRead: "t1", shows: "working" },
      { complete: "t1", shows: "working" },
    ],
    ends: "working",
  },
  {
    name: "the replacement still ends the chat after a read named the turn it replaced",
    resumeWith: "t1",
    steps: [
      { start: "t2", shows: "working" },
      { holdRead: true },
      { reconnect: true },
      { answerHeldRead: "t1", shows: "working" },
      { complete: "t1", shows: "working" },
      { complete: "t2" },
    ],
    ends: "idle",
  },
  {
    name: "a read naming a replaced turn after the replacement completed leaves the chat idle",
    resumeWith: "t1",
    steps: [
      { start: "t2" },
      { complete: "t2", shows: "idle" },
      { holdRead: true },
      { reconnect: true },
      { answerHeldRead: "t1", shows: "idle" },
    ],
    ends: "idle",
  },
];

/** Stop button and working indicator, which must always agree with each other. */
function shown() {
  const stop = screen.queryByLabelText("Stop") !== null;
  const indicator = screen.queryByRole("status") !== null;
  expect(stop).toBe(indicator);
  return stop ? "working" : "idle";
}

afterEach(() => {
  signalRHub.reset();
  vi.clearAllMocks();
  serverTurn = null;
  nextRead = "answers";
  heldRead = null;
  heldSend = null;
  localStorage.clear();
});

describe("ChatBubble turn state machine", () => {
  async function runCase({ resumeWith, steps, ends }: Case) {
    chatService.listHistory.mockResolvedValue([
      chatSummary(CHAT, "Past chat", { updatedAt: null }),
    ]);
    aiProviderService.getAll.mockResolvedValue([]);
    chatService.sendMessage.mockResolvedValue(null);
    chatService.interrupt.mockResolvedValue(undefined);
    // Every read answers with whatever the server would say at that moment, unless
    // a step is deliberately holding one open.
    chatService.getById.mockImplementation(() => {
      if (nextRead === "hangs") {
        nextRead = "answers";
        return new Promise<ChatSession>((resolve) => {
          heldRead = resolve;
        });
      }
      return Promise.resolve(chatSession(serverTurn));
    });

    serverTurn = resumeWith ?? null;
    const view = await renderChatBubble();
    await openChatPanel();
    await openChatFromList("Past chat");
    await screen.findByLabelText("Chat message");
    await waitFor(() => expect(chatService.getById).toHaveBeenCalled());
    await act(async () => {});

    expect(shown()).toBe(resumeWith ? "working" : "idle");

    for (const [i, step] of steps.entries()) {
      const where = `${i + 1}. ${JSON.stringify(step)}`;

      if ("sendPending" in step) {
        // The request hangs: the message is on its way and the server has not
        // registered its turn yet, which is the window everything else lands in.
        chatService.sendMessage.mockReturnValue(
          new Promise<string | null>((resolve, reject) => {
            heldSend = { accept: () => resolve(null), reject, turn: step.sendPending };
          }),
        );
        const input = screen.getByLabelText("Chat message");
        fireEvent.change(input, { target: { value: `message ` } });
        await act(async () => {
          fireEvent.click(screen.getByText("Send"));
        });
        expect(shown(), where).toBe("working");
        continue;
      }

      if ("settleSend" in step) {
        const held = heldSend!;
        heldSend = null;
        if (step.settleSend === "accepted") serverTurn = held.turn;
        await act(async () => {
          if (step.settleSend === "accepted") held.accept();
          else held.reject(new Error("Network error."));
        });
        await act(async () => {});
        chatService.sendMessage.mockResolvedValue(null);
        continue;
      }

      if ("send" in step) {
        // The request returns only once the runner has the turn registered, so
        // from here a read reports it.
        serverTurn = step.send;
        const input = screen.getByLabelText("Chat message");
        fireEvent.change(input, { target: { value: `message ${i}` } });
        await act(async () => {
          fireEvent.click(screen.getByText("Send"));
        });
        // A send always leaves the chat working: it has been accepted.
        expect(shown(), where).toBe("working");
        if (step.reads) expect(screen.getByRole("status").textContent, where).toContain(step.reads);
        continue;
      }

      if ("start" in step) {
        emit("ChatTurnStarted", {
          chatSessionId: step.start === "x1" ? "s2" : CHAT,
          turnId: step.start,
        });
      } else if ("progress" in step) {
        emit("ChatTurnProgress", {
          chatSessionId: step.progress === "x1" ? "s2" : CHAT,
          turnId: step.progress,
          delta: step.text,
        });
      } else if ("append" in step) {
        emit("ChatMessageAppended", {
          chatSessionId: CHAT,
          turnId: step.append,
          message: msg(`m${i}`, step.text, step.interrupted ?? false),
        });
      } else if ("complete" in step) {
        if (step.complete !== "x1" && serverTurn === step.complete) serverTurn = null;
        emit("ChatTurnCompleted", {
          chatSessionId: step.complete === "x1" ? "s2" : CHAT,
          turnId: step.complete,
          interrupted: false,
        });
      } else if ("elsewhere" in step) {
        emit("ChatTurnProgress", {
          chatSessionId: "s2",
          turnId: step.elsewhere,
          delta: "not mine",
        });
        emit("ChatMessageAppended", {
          chatSessionId: "s2",
          turnId: step.elsewhere,
          message: msg(`z${i}`, "someone else's reply"),
        });
        emit("ChatTurnCompleted", {
          chatSessionId: "s2",
          turnId: step.elsewhere,
          interrupted: false,
        });
        expect(screen.queryByText("not mine"), where).toBeNull();
        expect(screen.queryByText("someone else's reply"), where).toBeNull();
      } else if ("serverEnds" in step) {
        // Nothing is emitted: the turn is over and the client has no way to know
        // until it next asks.
        serverTurn = null;
      } else if ("interrupt" in step) {
        serverTurn = null;
        await act(async () => {
          fireEvent.click(screen.getByLabelText("Stop"));
        });
        expect(chatService.interrupt, where).toHaveBeenCalledWith(CHAT);
      } else if ("reconnect" in step) {
        for (const state of ["reconnecting", "connected"] as const) {
          signalRHub.setState(state);
          const tree = await chatBubbleTree();
          act(() => view.rerender(tree));
        }
        await act(async () => {});
      } else if ("holdRead" in step) {
        nextRead = "hangs";
      } else if ("answerHeldRead" in step) {
        const answer = heldRead;
        heldRead = null;
        expect(answer, `: no read was being held`).not.toBeNull();
        await act(async () => {
          answer!(chatSession(step.answerHeldRead));
        });
      }

      await act(async () => {});
      // Whatever the step was, the two controls still agree with each other — and
      // where the case pins a state for this point, they show it.
      const state = shown();
      if (step.shows) expect(state, where).toBe(step.shows);
      if (step.reads) expect(screen.getByRole("status").textContent, where).toContain(step.reads);
    }

    await waitFor(() => expect(shown()).toBe(ends));
  }

  test.each(CASES)("$name", runCase);
});
