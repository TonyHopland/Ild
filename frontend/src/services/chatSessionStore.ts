/**
 * Process-wide observable for the current chat session id (loop editor context,
 * ADR-0011). The chat session's lifecycle lives in the globally-mounted
 * `ChatBubble` (the only place sessions are started/ended), but the `LoopEditor`
 * route also needs the id so it can join the session's `/hubs/chat` group and
 * receive live loop edits. The editor may mount before the session exists, and a
 * user can end one session and start another while the editor stays open — so a
 * one-shot fetch is not enough. `ChatBubble` publishes every change here and the
 * editor subscribes, re-joining the right group whenever the id changes.
 */
let currentId: string | null = null;
const listeners = new Set<(id: string | null) => void>();

/** Publish the current chat session id (null when no session). ChatBubble owns this. */
export function setCurrentChatSessionId(id: string | null): void {
  if (id === currentId) return;
  currentId = id;
  for (const listener of listeners) listener(id);
}

/** The current chat session id as last published, or null when none. */
export function getCurrentChatSessionId(): string | null {
  return currentId;
}

/** Subscribe to chat-session-id changes. Returns an unsubscribe function. */
export function subscribeChatSessionId(listener: (id: string | null) => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}
