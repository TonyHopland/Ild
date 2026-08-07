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
import { getOpenLoopDocument } from "../utils/openLoopDocument";
import { setCurrentChatSessionId } from "../services/chatSessionStore";
import "./ChatBubble.css";

// Treat tiny pointer movements as a click, not a drag, so the icon still opens
// the panel when tapped.
const DRAG_THRESHOLD_PX = 4;

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
  const [busy, setBusy] = useState(false);
  // Set while a stop request is in flight, so a second click cannot fire another.
  const [stopping, setStopping] = useState(false);
  const [loaded, setLoaded] = useState(false);

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

  // Join the active chat's group whenever we are connected, and leave it on
  // resume/back so streamed turns only ever reach the chat currently open.
  useEffect(() => {
    if (connectionState !== "connected" || !session?.id) return;
    const id = session.id;
    // The server refuses a chat the caller does not own, so the invocation can
    // reject: log it rather than leaving an unhandled rejection. Nothing to show
    // the user — a chat that is not ours has no turns to stream here.
    void invoke("SubscribeToChat", id)?.catch((err) => console.error(err));
    return () => {
      void invoke("UnsubscribeFromChat", id)?.catch((err) => console.error(err));
    };
  }, [connectionState, session?.id, invoke]);

  // Publish the session id so other components (the LoopEditor) can join the same
  // chat group — including a session created or restarted after they mount.
  useEffect(() => {
    setCurrentChatSessionId(session?.id ?? null);
  }, [session?.id]);

  const upsertMessage = useCallback((message: ChatMessage) => {
    setMessages((prev) =>
      prev.some((m) => m.id === message.id)
        ? prev
        : [...prev, message].sort((a, b) => a.sequence - b.sequence),
    );
  }, []);

  useEffect(() => {
    const onAppended = (msg: { payload: ChatMessageAppendedPayload }) => {
      if (msg.payload.chatSessionId !== sessionIdRef.current) return;
      upsertMessage(msg.payload.message);
      if (msg.payload.message.role === "assistant") {
        setStreaming("");
        setBusy(false);
      }
    };
    const onProgress = (msg: { payload: ChatTurnProgressPayload }) => {
      if (msg.payload.chatSessionId !== sessionIdRef.current) return;
      setStreaming((prev) => prev + msg.payload.delta);
    };
    const onCompleted = (msg: { payload: ChatTurnCompletedPayload }) => {
      if (msg.payload.chatSessionId !== sessionIdRef.current) return;
      setStreaming("");
      setBusy(false);
    };

    on("ChatMessageAppended", onAppended);
    on("ChatTurnProgress", onProgress);
    on("ChatTurnCompleted", onCompleted);
    return () => {
      off("ChatMessageAppended", onAppended);
      off("ChatTurnProgress", onProgress);
      off("ChatTurnCompleted", onCompleted);
    };
  }, [on, off, upsertMessage]);

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
    setError(null);
    try {
      const created = await chatService.start(providerId, Array.from(tools));
      setSession(created);
      setMessages(created.messages);
      sessionIdRef.current = created.id;
    } catch (e) {
      setError((e as { message?: string })?.message ?? "Could not start chat.");
    }
  };

  const [input, setInput] = useState("");
  const send = async () => {
    const content = input.trim();
    if (!content || !session) return;
    setInput("");
    setBusy(true);
    try {
      // The open Loop Editor's live, possibly-unsaved document travels with each
      // message so the agent can read and edit the loop the user is looking at
      // (ADR-0011). Serialized to the same JSON the editor's import/export use.
      const openLoop = getOpenLoopDocument();
      const openLoopDocument = openLoop ? JSON.stringify(openLoop) : null;
      await chatService.sendMessage(
        session.id,
        content,
        openWorkItemIdRef.current,
        openLoopDocument,
      );
    } catch (e) {
      setBusy(false);
      setError((e as { message?: string })?.message ?? "Could not send message.");
    }
  };

  // Cancel the in-flight turn. `busy` is deliberately left alone: the server
  // persists the partial reply and announces it over the hub, so the normal
  // ChatMessageAppended/ChatTurnCompleted handlers end the turn just as they do
  // for one that finished on its own. The turn can also finish between render and
  // click, which makes the call a no-op (or a 404 on a chat already gone) —
  // nothing to report, so it is swallowed.
  const stop = async () => {
    if (!session || stopping) return;
    setStopping(true);
    try {
      await chatService.interrupt(session.id);
    } catch {
      /* nothing left to cancel */
    } finally {
      setStopping(false);
    }
  };

  // Drop the in-conversation view and return to the chat list. The chat is
  // retained and resumable — Back never deletes (ADR-0013). Refresh history so the
  // chat re-sorts to the top with its freshly-derived name.
  const backToList = useCallback(() => {
    setSession(null);
    setMessages([]);
    setStreaming("");
    setBusy(false);
    setError(null);
    setConfirmDeleteAll(false);
    sessionIdRef.current = null;
    void refreshHistory().catch(() => {});
  }, [refreshHistory]);

  // Resume a past chat: load its transcript and continue the same agent session.
  const resumeChat = async (id: string) => {
    setError(null);
    try {
      const resumed = await chatService.getById(id);
      setSession(resumed);
      setMessages(resumed.messages);
      setStreaming("");
      setBusy(false);
      sessionIdRef.current = resumed.id;
    } catch (e) {
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
