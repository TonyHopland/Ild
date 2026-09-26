import { useCallback, useEffect, useRef, useState } from "react";
import { useMatch } from "react-router";
import { useSignalR } from "../hooks/useSignalR";
import { useChatEnabled } from "../hooks/useChatEnabled";
import { aiProviderService, chatService } from "../services/auth";
import type {
  AiProvider,
  ChatMessage,
  ChatSession,
  ChatSessionSummary,
  ChatMessageAppendedPayload,
  ChatTurnProgressPayload,
  ChatTurnStartedPayload,
  ChatTurnCompletedPayload,
} from "../types";
import {
  clampFabPosition,
  clampPanelSize,
  loadFabPosition,
  loadPanelPosition,
  loadPanelSize,
  panelPosition,
  saveFabPosition,
  savePanelPosition,
  savePanelSize,
  viewportSize,
  type Point,
  type Size,
} from "./chatPlacement";
import MarkdownRenderer from "./MarkdownRenderer";
import ChatEditProposals from "./ChatEditProposals";
import { getOpenLoopDocument } from "../utils/openLoopDocument";
import { setCurrentChatSessionId } from "../services/chatSessionStore";
import "./ChatBubble.css";

// Treat tiny pointer movements as a click, not a drag, so the icon still opens
// the panel when tapped.
const DRAG_THRESHOLD_PX = 4;

// Stands in for the turn a message has just started, between posting it and the
// server naming that turn. A message interrupts rather than queues, so whatever
// turn id we were holding is already stale — and the chat is busy either way.
const PENDING_TURN = "pending";

// The v1 tool catalog (read/write/execute/ild). `ild` is the only default-on
// entry; the backend re-normalizes the selection against the provider type.
const TOOL_OPTIONS: { key: string; label: string; defaultOn: boolean }[] = [
  { key: "ild", label: "ILD features", defaultOn: true },
  { key: "read", label: "Read", defaultOn: false },
  { key: "write", label: "Write", defaultOn: false },
  { key: "execute", label: "Execute", defaultOn: false },
];

/**
 * Persistent chat bubble (ADR-0010) with retained chat history (ADR-0013).
 * Mounted globally so it survives navigation; chats live server-side. The bubble
 * lists the user's past chats and resumes any of them with its full transcript;
 * chats are retained until explicitly deleted (per-chat or "delete all").
 */
export default function ChatBubble() {
  const [open, setOpen] = useState(false);
  const [session, setSession] = useState<ChatSession | null>(null);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [streaming, setStreaming] = useState("");
  // Which turn wrote what is in the streamed buffer, so a delta from another turn
  // replaces it instead of appending to it.
  const streamingTurnRef = useRef<string | null>(null);
  // The turn the server has in flight for the open chat, or null when it is idle:
  // the one thing the working indicator and the stop button are drawn from. The
  // server owns this — inferring it from message events is what lost the stop
  // button the moment one turn handed over to the next.
  const [turn, setTurn] = useState<string | null>(null);
  const turnRef = useRef<string | null>(null);
  // Bumped by every turn change a read must not overrule — a hub event, a send,
  // leaving or opening a chat — so a snapshot taken before one of those can tell
  // that what it was asked about has moved on. Monotonic on purpose: a turn value
  // that left and came back is still a later epoch. A read applying its own
  // answer deliberately does not move it; reads are ordered against each other
  // below, and moving the epoch here would let one read silence a newer one.
  const epochRef = useRef(0);
  // Numbers each state read as it goes out, and records the newest one whose
  // answer has been applied. Reads are ordered by what has landed rather than by
  // what has been started, so a read that fails or never comes back cannot
  // silence the answer of one that did.
  const readRef = useRef(0);
  const appliedReadRef = useRef(0);
  // The send currently in flight and the chat it was sent to, or null when there is
  // none. Until it comes back the server may not have registered its turn yet, so
  // for that chat every other snapshot is answering about a chat it cannot know is
  // busy: only the send's own read, taken once the request has returned, may settle
  // what it put up. It says nothing about any other chat, which goes on reading its
  // own state as usual.
  const sendRef = useRef(0);
  const pendingSendRef = useRef<{ chatSessionId: string; send: number } | null>(null);
  // The stop request in flight and the chat it was sent to, or null when there is
  // none. Held by chat rather than as a bare flag because the bubble is one
  // component for every chat: a stop pressed in one of them must not disable the
  // button in another the user opens while it is still going. Numbered like the
  // send above, and for the same reason — the chat id alone does not say which stop
  // this is, so a stop coming back late would release a newer stop's claim and
  // re-enable the button underneath it, with that one still in flight.
  const stopRef = useRef(0);
  const [stoppingChat, setStoppingChat] = useState<{ chatSessionId: string; stop: number } | null>(
    null,
  );
  const stopping = stoppingChat !== null && stoppingChat.chatSessionId === session?.id;

  // Both claims above are held only while a request of this view's is still out,
  // and each is released by the request that made it — but a request that never
  // comes back never releases anything, and a claim belongs to the view that made
  // it rather than to the chat or to the process. So leaving a chat drops them.
  // Otherwise the next visit to that chat would discard the very read that settles
  // its controls, leaving a stop button and a working indicator that nothing can
  // clear, and would keep that button disabled for a stop nobody is waiting for.
  // Nothing needs releasing on unmount: these die with the component, and closing
  // the panel does not unmount it or leave the chat.
  const releaseRequestClaims = useCallback(() => {
    pendingSendRef.current = null;
    setStoppingChat(null);
  }, []);

  // Which visit to a chat view this is. A chat is opened by an awaited read, and
  // the list stays on screen while that read is in flight, so two opens can race:
  // click one chat, then another before the first answers. Without this the slower
  // answer installs its chat over the one the user is now looking at — transcript,
  // turn and all — and the stop button on that view would interrupt a turn in a
  // chat they never opened. Counted rather than compared by id, because the second
  // open may be the same chat as the first; bumped by leaving as well, so an open
  // the user walked away from cannot install itself either.
  const visitRef = useRef(0);
  const [loaded, setLoaded] = useState(false);
  const busy = turn !== null;

  // Retained chat history (ADR-0013): the user's past chats, shown under Start
  // chat. `confirmDeleteAll` gates the wipe-all action behind a confirmation.
  const [history, setHistory] = useState<ChatSessionSummary[]>([]);
  const [confirmDeleteAll, setConfirmDeleteAll] = useState(false);

  // Start form
  const [providers, setProviders] = useState<AiProvider[]>([]);
  const [providerId, setProviderId] = useState("");
  const [tools, setTools] = useState<Set<string>>(
    () => new Set(TOOL_OPTIONS.filter((t) => t.defaultOn).map((t) => t.key)),
  );
  const [error, setError] = useState<string | null>(null);

  const { connectionState, on, off, invoke } = useSignalR("/hubs/chat");
  const sessionIdRef = useRef<string | null>(null);
  const scrollRef = useRef<HTMLDivElement | null>(null);

  // The ambient per-turn Chat Context (ADR-0011): the work item the user
  // currently has open, read from the route. Sent with each message so the agent
  // can act on whatever the human is looking at. Held in a ref so `send` always
  // reads the latest value without re-creating the callback.
  const openWorkItemMatch = useMatch("/taskboard/:workItemId");
  const openWorkItemId = openWorkItemMatch?.params.workItemId ?? null;
  const openWorkItemIdRef = useRef<string | null>(openWorkItemId);
  openWorkItemIdRef.current = openWorkItemId;

  // Placement: a draggable icon position and a resizable panel size, both
  // persisted and kept inside the viewport.
  const chatEnabled = useChatEnabled();
  const [fabPos, setFabPos] = useState<Point>(loadFabPosition);
  const [panelSize, setPanelSize] = useState<Size>(loadPanelSize);
  // The panel's own position once the user drags its header; until then it stays
  // anchored to the icon (null).
  const [panelOverride, setPanelOverride] = useState<Point | null>(loadPanelPosition);
  // Set while a drag exceeds the threshold so the trailing click does not also
  // open the panel.
  const draggedRef = useRef(false);
  // Latest panel size, so the window-resize handler can re-clamp the panel
  // position without re-subscribing on every size change.
  const panelSizeRef = useRef(panelSize);
  panelSizeRef.current = panelSize;

  // Persist placement changes and re-clamp into view whenever the window resizes.
  useEffect(() => {
    saveFabPosition(fabPos);
  }, [fabPos]);
  useEffect(() => {
    savePanelSize(panelSize);
  }, [panelSize]);
  useEffect(() => {
    if (panelOverride) savePanelPosition(panelOverride);
  }, [panelOverride]);
  useEffect(() => {
    const onResize = () => {
      const vp = viewportSize();
      setFabPos((p) => clampFabPosition(p, vp));
      setPanelSize((s) => clampPanelSize(s, vp));
      setPanelOverride((p) => (p ? panelPosition(p, panelSizeRef.current, vp) : p));
    };
    window.addEventListener("resize", onResize);
    return () => window.removeEventListener("resize", onResize);
  }, []);

  const startDrag = useCallback(
    (e: React.PointerEvent) => {
      draggedRef.current = false;
      const origin = { px: e.clientX, py: e.clientY, ox: fabPos.x, oy: fabPos.y };
      const onMove = (ev: PointerEvent) => {
        const dx = ev.clientX - origin.px;
        const dy = ev.clientY - origin.py;
        if (Math.abs(dx) > DRAG_THRESHOLD_PX || Math.abs(dy) > DRAG_THRESHOLD_PX) {
          draggedRef.current = true;
        }
        setFabPos(clampFabPosition({ x: origin.ox + dx, y: origin.oy + dy }, viewportSize()));
      };
      const onUp = () => {
        window.removeEventListener("pointermove", onMove);
        window.removeEventListener("pointerup", onUp);
      };
      window.addEventListener("pointermove", onMove);
      window.addEventListener("pointerup", onUp);
    },
    [fabPos.x, fabPos.y],
  );

  const startResize = useCallback(
    (e: React.PointerEvent) => {
      e.preventDefault();
      const origin = { px: e.clientX, py: e.clientY, w: panelSize.width, h: panelSize.height };
      const onMove = (ev: PointerEvent) => {
        const vp = viewportSize();
        const next = clampPanelSize(
          {
            width: origin.w + (ev.clientX - origin.px),
            height: origin.h + (ev.clientY - origin.py),
          },
          vp,
        );
        setPanelSize(next);
        // Keep a moved panel on-screen as it grows toward the viewport edge.
        setPanelOverride((p) => (p ? panelPosition(p, next, vp) : p));
      };
      const onUp = () => {
        window.removeEventListener("pointermove", onMove);
        window.removeEventListener("pointerup", onUp);
      };
      window.addEventListener("pointermove", onMove);
      window.addEventListener("pointerup", onUp);
    },
    [panelSize.width, panelSize.height],
  );

  const startHeaderDrag = useCallback(
    (e: React.PointerEvent) => {
      // Let the header's own buttons (End chat / close) work without dragging.
      if ((e.target as HTMLElement).closest("button")) return;
      const base = panelOverride ?? panelPosition(fabPos, panelSize, viewportSize());
      const origin = { px: e.clientX, py: e.clientY, ox: base.x, oy: base.y };
      const onMove = (ev: PointerEvent) => {
        setPanelOverride(
          panelPosition(
            { x: origin.ox + (ev.clientX - origin.px), y: origin.oy + (ev.clientY - origin.py) },
            panelSize,
            viewportSize(),
          ),
        );
      };
      const onUp = () => {
        window.removeEventListener("pointermove", onMove);
        window.removeEventListener("pointerup", onUp);
      };
      window.addEventListener("pointermove", onMove);
      window.addEventListener("pointerup", onUp);
    },
    [panelOverride, fabPos, panelSize],
  );

  const refreshHistory = useCallback(async () => {
    const chats = await chatService.listHistory();
    setHistory(chats);
  }, []);

  // Load the chat history once, so the list is ready when the bubble opens.
  useEffect(() => {
    let cancelled = false;
    void refreshHistory()
      .catch(() => {})
      .finally(() => {
        if (!cancelled) setLoaded(true);
      });
    return () => {
      cancelled = true;
    };
  }, [refreshHistory]);

  const upsertMessage = useCallback((message: ChatMessage) => {
    setMessages((prev) =>
      prev.some((m) => m.id === message.id)
        ? prev
        : [...prev, message].sort((a, b) => a.sequence - b.sequence),
    );
  }, []);

  // Whether an event belongs to the turn whose text is on screen.
  //
  // The placeholder matches anything, and lives only from the click until the send
  // request answers with the turn it started. That is deliberate rather than
  // approximate: within that window the server has not registered the message yet,
  // so the turn being displaced is still running and still the turn on screen — its
  // deltas are the text the user is reading, and rejecting them would tear a hole
  // in it, including on a send that never arrives, where that turn simply carries
  // on. What it streamed in the window is dropped wholesale when the turn value
  // moves on, because a start or an answer naming a different turn clears the
  // buffer, so its text is never grown on top of.
  //
  // Once the send has answered there is no placeholder to abuse: from then on this
  // chat's turn is known by id, whether or not the start broadcast ever arrived.
  const isCurrentTurn = useCallback(
    (turnId: string) => turnRef.current === PENDING_TURN || turnRef.current === turnId,
    [],
  );

  // The only writer of the turn value, so the ref the once-registered hub
  // handlers read never drifts from the state the view renders.
  const setTurnValue = useCallback((next: string | null) => {
    turnRef.current = next;
    setTurn(next);
  }, []);

  // Every turn change except a state read applying its own answer: those are the
  // changes a snapshot already in flight must not be allowed to undo.
  const applyTurn = useCallback(
    (next: string | null) => {
      epochRef.current += 1;
      setTurnValue(next);
    },
    [setTurnValue],
  );

  // The streamed text belongs to the turn that wrote it. A delta from a different
  // turn starts the buffer over rather than adding to it, so two turns' replies can
  // never run together on screen — not while the placeholder is up, and not if a
  // start broadcast goes missing. Deltas append only within one turn.
  const streamDelta = useCallback((turnId: string, delta: string) => {
    const carryOn = streamingTurnRef.current === turnId;
    streamingTurnRef.current = turnId;
    setStreaming((prev) => (carryOn ? prev + delta : delta));
  }, []);

  /** Nothing should be streaming at all: a turn ended, or the view left the chat. */
  const clearStream = useCallback(() => {
    streamingTurnRef.current = null;
    setStreaming("");
  }, []);

  // Drops a partial written by any turn other than this one, and keeps one written
  // by this turn: learning the name of the turn already streaming must not throw
  // away the text it has streamed so far.
  const clearStreamUnlessFrom = useCallback(
    (turnId: string | null) => {
      if (streamingTurnRef.current !== null && streamingTurnRef.current !== turnId) clearStream();
    },
    [clearStream],
  );

  // Re-read the chat whenever the client may be holding a belief the server has
  // already contradicted: on every join of its live stream, after a stop the
  // server accepted, and after a send that failed. Nothing else can tell a bubble
  // that has just loaded, or just come back from an outage, that a turn is still
  // running — or that the one it was watching is long over.
  const refreshActiveState = useCallback(
    async (id: string, forSend = 0) => {
      const epoch = epochRef.current;
      const read = ++readRef.current;
      const view = await chatService.getById(id);
      // A newer read has already answered, so ours is stale by construction: a
      // join and a stop's own reconciliation can be in flight together, and
      // without this the first to answer would win however old it was, leaving a
      // stopped chat holding its stop button. Judged on answers rather than on
      // requests, so a read that fails or never returns takes nothing with it.
      if (read <= appliedReadRef.current) return;
      // Discard an answer about a chat we have left, or one taken before a turn
      // we have since learned about: a snapshot may never overrule a newer fact.
      if (sessionIdRef.current !== id || epochRef.current !== epoch) return;
      // This chat has a send in flight and this is not its read. The request has
      // not come back, so the server may not have registered its turn when this
      // snapshot was taken, and an idle answer would take the controls off a chat
      // that is about to be — or already is — working. Its own read settles it
      // instead. A send waiting in another chat holds nothing back here.
      const pendingSend = pendingSendRef.current;
      if (pendingSend?.chatSessionId === id && pendingSend.send !== forSend) return;
      appliedReadRef.current = read;

      // Merged in one pass, never replaced. The snapshot predates whatever
      // arrived over the hub while it was in flight, so it may add what we
      // missed but must not drop what we know — the new turn's own user message,
      // say. A whole transcript arrives at once here, unlike the single message
      // an append carries, so it is merged as one.
      setMessages((prev) => {
        const known = new Set(prev.map((m) => m.id));
        const missing = view.messages.filter((m) => !known.has(m.id));
        return missing.length === 0
          ? prev
          : [...prev, ...missing].sort((a, b) => a.sequence - b.sequence);
      });

      const active = view.activeTurnId ?? null;
      // A streamed partial belongs to the turn that produced it, and only that
      // turn's own finalized reply replaces it. If the server no longer names
      // that turn, the text on screen is from a turn that has ended.
      clearStreamUnlessFrom(active);
      setTurnValue(active);
    },
    [setTurnValue],
  );

  // Join the active chat's group whenever we are connected, and leave it on
  // resume/back so streamed turns only ever reach the chat currently open.
  useEffect(() => {
    if (connectionState !== "connected" || !session?.id) return;
    const id = session.id;
    void (async () => {
      try {
        // The server refuses a chat the caller does not own, so the invocation
        // can reject: log it rather than leaving an unhandled rejection. Nothing
        // to show the user — a chat that is not ours has no turns to stream here,
        // and nothing to re-read either.
        await invoke("SubscribeToChat", id);
      } catch (err) {
        console.error(err);
        return;
      }
      try {
        // Only once the join has taken effect: a turn that ended in the gap
        // between reading and joining would be missed for good.
        await refreshActiveState(id);
      } catch (err) {
        // The view keeps what it had; the next join asks again.
        console.error(err);
      }
    })();
    return () => {
      void invoke("UnsubscribeFromChat", id)?.catch((err) => console.error(err));
    };
  }, [connectionState, session?.id, invoke, refreshActiveState]);

  // Publish the session id so other components (the LoopEditor) can join the same
  // chat group — including a session created or restarted after they mount.
  useEffect(() => {
    setCurrentChatSessionId(session?.id ?? null);
  }, [session?.id]);

  useEffect(() => {
    const onAppended = (msg: { payload: ChatMessageAppendedPayload }) => {
      if (msg.payload.chatSessionId !== sessionIdRef.current) return;
      upsertMessage(msg.payload.message);
      // The transcript takes every finalized message, whichever turn wrote it — an
      // interrupted reply belongs in it as much as a complete one. The streamed
      // partial it replaces is its own, and only its own: a reply finalized by the
      // turn that was displaced says nothing about what the turn now streaming has
      // written, so it must not wipe it.
      if (
        msg.payload.message.role === "assistant" &&
        streamingTurnRef.current === msg.payload.turnId
      ) {
        clearStream();
      }
    };
    const onProgress = (msg: { payload: ChatTurnProgressPayload }) => {
      if (msg.payload.chatSessionId !== sessionIdRef.current) return;
      // Deltas append, so one from a turn that is no longer current would grow
      // the live turn's reply on top of a dead one's.
      if (!isCurrentTurn(msg.payload.turnId)) return;
      streamDelta(msg.payload.turnId, msg.payload.delta);
    };
    const onStarted = (msg: { payload: ChatTurnStartedPayload }) => {
      if (msg.payload.chatSessionId !== sessionIdRef.current) return;
      // The streamed buffer holds whatever arrived while the turn being replaced
      // was the current one, and deltas append: carrying it over would grow the
      // new turn's reply on top of the old one's. Its own finalized message
      // usually clears it, but that message can be missed in an outage, and a
      // snapshot afterwards sees a partial that matches the running turn and
      // keeps it. Dropped only when the turn really changes, so a start that
      // merely confirms the turn we are already on leaves its text alone.
      clearStreamUnlessFrom(msg.payload.turnId);
      applyTurn(msg.payload.turnId);
    };
    const onCompleted = (msg: { payload: ChatTurnCompletedPayload }) => {
      if (msg.payload.chatSessionId !== sessionIdRef.current) return;
      // Only the turn we believe is running can end the turn: hub sends carry no
      // ordering guarantee between turns, so a replaced turn's completion can
      // arrive after its successor has already announced itself.
      if (msg.payload.turnId !== turnRef.current) return;
      clearStream();
      applyTurn(null);
    };

    on("ChatMessageAppended", onAppended);
    on("ChatTurnProgress", onProgress);
    on("ChatTurnStarted", onStarted);
    on("ChatTurnCompleted", onCompleted);
    return () => {
      off("ChatMessageAppended", onAppended);
      off("ChatTurnProgress", onProgress);
      off("ChatTurnStarted", onStarted);
      off("ChatTurnCompleted", onCompleted);
    };
  }, [on, off, upsertMessage, applyTurn, isCurrentTurn]);

  // Keep the transcript scrolled to the newest content. `scrollTo` is absent in
  // jsdom, so guard the call rather than assume it exists.
  useEffect(() => {
    scrollRef.current?.scrollTo?.({ top: scrollRef.current.scrollHeight });
  }, [messages, streaming]);

  const openPanel = useCallback(() => {
    setOpen(true);
  }, []);

  // Load the AI providers whenever the start form needs them — on first open and
  // again after ending a chat — so the list is never left empty waiting for a
  // close/reopen (e.g. when the first load failed at startup). Tied to the start
  // form being shown rather than to the open-from-closed click, and skipped once
  // providers are loaded or a chat is active.
  useEffect(() => {
    if (!open || session || providers.length > 0) return;
    let cancelled = false;
    void (async () => {
      try {
        const loaded = await aiProviderService.getAll();
        if (cancelled) return;
        setProviders(loaded);
        // Pre-select the default provider so a new chat is ready to start.
        const fallback = loaded.find((p) => p.isDefault);
        if (fallback) setProviderId(fallback.id);
      } catch {
        if (!cancelled) setError("Could not load AI providers.");
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [open, session, providers.length]);

  const onFabClick = useCallback(() => {
    // Swallow the click that ends a drag; a genuine click re-arms and opens.
    if (draggedRef.current) {
      draggedRef.current = false;
      return;
    }
    openPanel();
  }, [openPanel]);

  const toggleTool = (key: string) => {
    setTools((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  };

  const startChat = async () => {
    if (!providerId) {
      setError("Pick an AI provider first.");
      return;
    }
    const visit = ++visitRef.current;
    setError(null);
    try {
      const created = await chatService.start(providerId, Array.from(tools));
      if (visitRef.current !== visit) return;
      setSession(created);
      setMessages(created.messages);
      applyTurn(created.activeTurnId ?? null);
      sessionIdRef.current = created.id;
      releaseRequestClaims();
    } catch (e) {
      if (visitRef.current !== visit) return;
      setError((e as { message?: string })?.message ?? "Could not start chat.");
    }
  };

  const [input, setInput] = useState("");
  const send = async () => {
    const content = input.trim();
    if (!content || !session) return;
    setInput("");
    // The message interrupts whatever was running, so the chat is busy from here
    // whichever turn the server ends up naming. The turn being displaced is kept:
    // a send that never reaches the server displaces nothing, and only the turn
    // value from before it can say so.
    const displaced = turnRef.current;
    const visit = visitRef.current;
    applyTurn(PENDING_TURN);
    const pendingEpoch = epochRef.current;
    const pendingRead = appliedReadRef.current;
    // Claims the pending state for this send. Until it comes back, no other
    // snapshot may settle or clear what it just put up: a join or a reconnect can
    // read the chat before the server has registered this turn, and an idle answer
    // would take the controls away while the message is still on its way.
    const mySend = ++sendRef.current;
    pendingSendRef.current = { chatSessionId: session.id, send: mySend };
    try {
      // The open Loop Editor's live, possibly-unsaved document travels with each
      // message so the agent can read and edit the loop the user is looking at
      // (ADR-0011). Serialized to the same JSON the editor's import/export use.
      const openLoop = getOpenLoopDocument();
      const openLoopDocument = openLoop ? JSON.stringify(openLoop) : null;
      const startedTurn = await chatService.sendMessage(
        session.id,
        content,
        openWorkItemIdRef.current,
        openLoopDocument,
      );
      // The answer names the turn it started, which retires the placeholder as soon
      // as the request comes back: from here the view knows its turn by id, so the
      // events of the turn this one displaced — still finalizing its own reply — are
      // told apart from this turn's own, which a placeholder matching anything
      // cannot do.
      //
      // Applied only if nothing has moved the turn on since: a start or a completion
      // arriving while the request was in flight knows better than this does, and
      // the epoch is what says one has. The name may also be missing, which the API
      // does not do — then the read below settles it, exactly as it used to.
      // Any falsy answer counts as no answer — a turn id is a non-empty string, and
      // an absent one must fall through to the read below rather than be applied as
      // the turn.
      if (
        startedTurn &&
        sessionIdRef.current === session.id &&
        visitRef.current === visit &&
        sendRef.current === mySend &&
        epochRef.current === pendingEpoch
      ) {
        // Anything the displaced turn streamed into the buffer goes now that this
        // turn is named, so its tail is never read as the start of this turn's reply.
        clearStreamUnlessFrom(startedTurn);
        applyTurn(startedTurn);
      }
      // Read back as well, because the answer says which turn started, not what has
      // become of it: broadcasts are dropped on failure by design, so a turn that
      // has since ended without this client hearing would leave a stop button on an
      // idle chat. The read carries the same guards as any other, so it cannot undo
      // a turn learned since it was issued.
      await refreshActiveState(session.id, mySend).catch((err) => console.error(err));
    } catch (e) {
      // Only where it happened: this send belongs to a chat the user may have left
      // by now, and its failure is not news in whatever chat is open instead.
      if (sessionIdRef.current === session.id) {
        setError((e as { message?: string })?.message ?? "Could not send message.");
      }
      // Put back the turn this send claimed to replace before anything reads the
      // turn value again: the send may never have reached the runner, and that
      // turn is then still running. Skipped once anything else has moved the turn
      // on — a start announced while this send was unwinding, or a state read
      // that has landed since, both of which know better than this does.
      if (epochRef.current === pendingEpoch && appliedReadRef.current === pendingRead) {
        applyTurn(displaced);
      }
      // The message may still have reached the runner, and a turn it did not
      // reach may be running regardless — so ask the server rather than trusting
      // either guess. On failure the view keeps the turn it had before the send.
      try {
        await refreshActiveState(session.id, mySend);
      } catch (err) {
        console.error(err);
      }
    } finally {
      // Released only by the send that claimed it, so a later send that has already
      // taken over keeps its own claim.
      if (pendingSendRef.current?.send === mySend) pendingSendRef.current = null;
    }
  };

  // Cancel the in-flight turn. The turn value is deliberately left alone: the
  // server persists the partial reply and announces it over the hub, so the turn
  // ends through the same ChatTurnCompleted the client waits for when one
  // finishes on its own. The turn can also finish between render and click, which
  // makes the call a no-op (or a 404 on a chat already gone) — nothing to report,
  // so it is swallowed.
  const stop = async () => {
    if (!session || stopping) return;
    const id = session.id;
    const myStop = ++stopRef.current;
    setStoppingChat({ chatSessionId: id, stop: myStop });
    let accepted = false;
    try {
      await chatService.interrupt(id);
      accepted = true;
    } catch {
      /* nothing left to cancel */
    } finally {
      // Released only by the stop that claimed it, so a stop that comes back late —
      // after the user left the chat and pressed stop again in it — leaves the claim
      // of the one still in flight alone, rather than re-enabling its button.
      setStoppingChat((current) => (current?.stop === myStop ? null : current));
    }

    // Only a stop the server accepted tells us anything: by then it may have
    // ended the turn without its completion ever reaching us. A rejected one
    // changes nothing, and must leave the button live for another attempt.
    if (accepted) await refreshActiveState(id).catch((err) => console.error(err));
  };

  // Drop the in-conversation view and return to the chat list. The chat is
  // retained and resumable — Back never deletes (ADR-0013). Refresh history so the
  // chat re-sorts to the top with its freshly-derived name.
  const backToList = useCallback(() => {
    visitRef.current += 1;
    setSession(null);
    setMessages([]);
    clearStream();
    applyTurn(null);
    setError(null);
    setConfirmDeleteAll(false);
    sessionIdRef.current = null;
    releaseRequestClaims();
    void refreshHistory().catch(() => {});
  }, [refreshHistory, applyTurn, releaseRequestClaims]);

  // Resume a past chat: load its transcript and continue the same agent session.
  const resumeChat = async (id: string) => {
    const visit = ++visitRef.current;
    setError(null);
    try {
      const resumed = await chatService.getById(id);
      // Another chat has been opened since, or the user has gone back to the list:
      // this answer is about a visit that is over and installs nothing.
      if (visitRef.current !== visit) return;
      setSession(resumed);
      setMessages(resumed.messages);
      clearStream();
      // A chat opened mid-turn shows it running straight away, stop button and
      // all — the transcript is not the only thing being resumed.
      applyTurn(resumed.activeTurnId ?? null);
      sessionIdRef.current = resumed.id;
      // Opened fresh, so it starts with no claim of its own — the same rule from
      // the other end, for a chat entered by any path that did not go via the list.
      releaseRequestClaims();
    } catch (e) {
      if (visitRef.current !== visit) return;
      setError((e as { message?: string })?.message ?? "Could not open chat.");
    }
  };

  const deleteChat = async (id: string) => {
    try {
      await chatService.deleteOne(id);
    } catch {
      /* even on error, drop it locally — it is gone or never existed */
    }
    setHistory((prev) => prev.filter((c) => c.id !== id));
  };

  const deleteAllChats = async () => {
    setConfirmDeleteAll(false);
    try {
      await chatService.deleteAll();
    } catch {
      /* even on error, clear the list — the chats are gone or never existed */
    }
    setHistory([]);
  };

  if (!chatEnabled) {
    return null;
  }

  if (!open) {
    return (
      <button
        type="button"
        className="chat-bubble-fab"
        aria-label="Open chat"
        style={{ left: fabPos.x, top: fabPos.y }}
        onPointerDown={startDrag}
        onClick={onFabClick}
      >
        💬
      </button>
    );
  }

  const panelPos = panelPosition(panelOverride ?? fabPos, panelSize, viewportSize());

  return (
    <div
      className="chat-panel"
      role="dialog"
      aria-label="AI chat"
      style={{
        left: panelPos.x,
        top: panelPos.y,
        width: panelSize.width,
        height: panelSize.height,
      }}
    >
      <div className="chat-panel-header" onPointerDown={startHeaderDrag}>
        <span className="chat-panel-title">{session?.name ?? "AI Chat"}</span>
        <div className="chat-panel-header-actions">
          {session && (
            <button type="button" className="chat-link-btn" onClick={backToList}>
              ← Back
            </button>
          )}
          <button
            type="button"
            className="chat-link-btn"
            aria-label="Close chat"
            onClick={() => setOpen(false)}
          >
            ✕
          </button>
        </div>
      </div>

      {error && <div className="chat-error">{error}</div>}

      {!loaded ? (
        <div className="chat-panel-body chat-muted">Loading…</div>
      ) : !session ? (
        <div className="chat-panel-body chat-start">
          <label className="chat-field-label" htmlFor="chat-provider">
            AI provider
          </label>
          <select
            id="chat-provider"
            className="chat-select"
            value={providerId}
            onChange={(e) => setProviderId(e.target.value)}
          >
            <option value="">Select a provider…</option>
            {providers.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name} ({p.type})
              </option>
            ))}
          </select>

          <span className="chat-field-label">Tools</span>
          <div className="chat-tools">
            {TOOL_OPTIONS.map((t) => (
              <label key={t.key} className="chat-tool">
                <input
                  type="checkbox"
                  checked={tools.has(t.key)}
                  onChange={() => toggleTool(t.key)}
                />
                {t.label}
              </label>
            ))}
          </div>

          <button type="button" className="chat-primary-btn" onClick={() => void startChat()}>
            Start chat
          </button>

          {history.length > 0 && (
            <div className="chat-history">
              <div className="chat-history-head">
                <span className="chat-field-label">Past chats</span>
                {confirmDeleteAll ? (
                  <span
                    className="chat-history-confirm"
                    role="alertdialog"
                    aria-label="Delete all chats?"
                  >
                    Delete all chats?
                    <button
                      type="button"
                      className="chat-link-btn chat-danger"
                      onClick={() => void deleteAllChats()}
                    >
                      Delete all
                    </button>
                    <button
                      type="button"
                      className="chat-link-btn"
                      onClick={() => setConfirmDeleteAll(false)}
                    >
                      Cancel
                    </button>
                  </span>
                ) : (
                  <button
                    type="button"
                    className="chat-link-btn chat-danger"
                    onClick={() => setConfirmDeleteAll(true)}
                  >
                    Delete all
                  </button>
                )}
              </div>
              <ul className="chat-history-list">
                {history.map((c) => (
                  <li key={c.id} className="chat-history-row">
                    <button
                      type="button"
                      className="chat-history-open"
                      onClick={() => void resumeChat(c.id)}
                    >
                      <span className="chat-history-name">{c.name ?? "New chat"}</span>
                      <span className="chat-history-date">
                        {new Date(c.updatedAt ?? c.createdAt).toLocaleString()}
                      </span>
                    </button>
                    <button
                      type="button"
                      className="chat-link-btn chat-danger"
                      aria-label={`Delete chat ${c.name ?? "New chat"}`}
                      onClick={() => void deleteChat(c.id)}
                    >
                      ✕
                    </button>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </div>
      ) : (
        <>
          <div className="chat-panel-body" ref={scrollRef}>
            {messages.map((m) => (
              <div key={m.id} className={`chat-msg chat-msg-${m.role}`}>
                <MarkdownRenderer content={m.content} className="chat-msg-content" />
                {m.interrupted && <span className="chat-interrupted">interrupted</span>}
              </div>
            ))}
            {session && <ChatEditProposals key={session.id} chatSessionId={session.id} />}
            {streaming && (
              <div className="chat-msg chat-msg-assistant chat-msg-streaming">
                <MarkdownRenderer content={streaming} className="chat-msg-content" />
              </div>
            )}
            {/* Visible for the whole turn — including while text streams — so it
                is clear the agent is still working rather than done. */}
            {busy && (
              <div className="chat-muted chat-typing" role="status">
                {streaming ? "Responding" : "Thinking"}
                <span className="chat-typing-dots" aria-hidden="true" />
              </div>
            )}
          </div>

          <form
            className="chat-input-row"
            onSubmit={(e) => {
              e.preventDefault();
              void send();
            }}
          >
            <input
              className="chat-input"
              placeholder="Message…"
              value={input}
              onChange={(e) => setInput(e.target.value)}
              aria-label="Chat message"
            />
            {/* Only offered while a turn is in flight — the same window the
                Thinking/Responding indicator covers. */}
            {busy && (
              <button
                type="button"
                className="chat-stop-btn"
                aria-label="Stop"
                disabled={stopping}
                onClick={() => void stop()}
              >
                <svg viewBox="0 0 16 16" width="12" height="12" aria-hidden="true">
                  <rect x="0" y="0" width="16" height="16" rx="2" />
                </svg>
              </button>
            )}
            <button type="submit" className="chat-primary-btn" disabled={!input.trim()}>
              Send
            </button>
          </form>
        </>
      )}

      <div
        className="chat-resize-handle"
        role="button"
        tabIndex={-1}
        aria-label="Resize chat"
        onPointerDown={startResize}
      />
    </div>
  );
}
