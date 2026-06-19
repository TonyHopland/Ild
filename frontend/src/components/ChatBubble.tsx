import { useCallback, useEffect, useRef, useState } from "react";
import { useSignalR } from "../hooks/useSignalR";
import { aiProviderService, chatService } from "../services/auth";
import type {
  AiProvider,
  ChatMessage,
  ChatSession,
  ChatMessageAppendedPayload,
  ChatTurnProgressPayload,
  ChatTurnCompletedPayload,
} from "../types";
import MarkdownRenderer from "./MarkdownRenderer";
import "./ChatBubble.css";

// The v1 tool catalog (read/write/execute/ild). `ild` is the only default-on
// entry; the backend re-normalizes the selection against the provider type.
const TOOL_OPTIONS: { key: string; label: string; defaultOn: boolean }[] = [
  { key: "ild", label: "ILD work items", defaultOn: true },
  { key: "read", label: "Read", defaultOn: false },
  { key: "write", label: "Write", defaultOn: false },
  { key: "execute", label: "Execute", defaultOn: false },
];

/**
 * Persistent per-user chat bubble (ADR-0010). Mounted globally so it survives
 * navigation; the session itself lives server-side and rehydrates on reload.
 */
export default function ChatBubble() {
  const [open, setOpen] = useState(false);
  const [session, setSession] = useState<ChatSession | null>(null);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [streaming, setStreaming] = useState("");
  const [busy, setBusy] = useState(false);
  const [loaded, setLoaded] = useState(false);

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

  // Load any existing session once, so the transcript rehydrates on reload.
  useEffect(() => {
    let cancelled = false;
    void chatService
      .get()
      .then((s) => {
        if (cancelled) return;
        setSession(s);
        setMessages(s?.messages ?? []);
        sessionIdRef.current = s?.id ?? null;
      })
      .catch(() => {})
      .finally(() => {
        if (!cancelled) setLoaded(true);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  // Subscribe to the session's group whenever we are connected and have a session.
  useEffect(() => {
    if (connectionState === "connected" && session?.id) {
      void invoke("SubscribeToChat", session.id);
    }
  }, [connectionState, session?.id, invoke]);

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

  const openPanel = useCallback(async () => {
    setOpen(true);
    if (!session && providers.length === 0) {
      try {
        setProviders(await aiProviderService.getAll());
      } catch {
        setError("Could not load AI providers.");
      }
    }
  }, [session, providers.length]);

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
      await chatService.sendMessage(content);
    } catch (e) {
      setBusy(false);
      setError((e as { message?: string })?.message ?? "Could not send message.");
    }
  };

  const endChat = async () => {
    try {
      await chatService.end();
    } catch {
      /* even on error, drop local state — the session is gone or never existed */
    }
    setSession(null);
    setMessages([]);
    setStreaming("");
    setBusy(false);
    sessionIdRef.current = null;
  };

  if (!open) {
    return (
      <button
        type="button"
        className="chat-bubble-fab"
        aria-label="Open chat"
        onClick={() => void openPanel()}
      >
        💬
      </button>
    );
  }

  return (
    <div className="chat-panel" role="dialog" aria-label="AI chat">
      <div className="chat-panel-header">
        <span className="chat-panel-title">AI Chat</span>
        <div className="chat-panel-header-actions">
          {session && (
            <button type="button" className="chat-link-btn" onClick={() => void endChat()}>
              End chat
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
            {busy && !streaming && <div className="chat-muted chat-typing">thinking…</div>}
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
            <button type="submit" className="chat-primary-btn" disabled={!input.trim()}>
              Send
            </button>
          </form>
        </>
      )}
    </div>
  );
}
