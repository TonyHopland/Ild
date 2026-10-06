import { useEffect, useState } from "react";
import { chatService } from "../services/auth";
import type { ChatSessionSummary } from "../types";
import { formatRelativeTime } from "../utils/relativeTime";
import { RenameForm, UnreadDot, useTitleEdit, type RenameChat } from "./ChatRename";

const SEARCH_DEBOUNCE_MS = 300;
const CLOCK_TICK_MS = 60_000;

const shownName = (chat: ChatSessionSummary) => chat.name ?? "Untitled chat";

/** One chat in the list, renamable in place. Keyed by the chat. */
function ChatSessionRow({
  chat,
  current,
  now,
  onOpen,
  onDelete,
  renameChat,
  renaming,
  onFavorite,
  starring,
}: {
  chat: ChatSessionSummary;
  current: boolean;
  now: number;
  onOpen: () => void;
  onDelete: () => void;
  renameChat: RenameChat;
  renaming: boolean;
  onFavorite: () => void;
  starring: boolean;
}) {
  const { editing, begin, closer } = useTitleEdit();
  const name = shownName(chat);
  const lastActivity = chat.updatedAt ?? chat.createdAt;
  return (
    <li className="chat-history-row">
      {editing !== null ? (
        <RenameForm
          key={editing}
          chatSessionId={chat.id}
          initial={chat.name ?? ""}
          renameChat={renameChat}
          blocked={renaming}
          onClose={closer(editing)}
        />
      ) : (
        <>
          <button
            type="button"
            className={`chat-link-btn chat-star${chat.isFavorite ? " chat-star-on" : ""}`}
            aria-label={`Favorite chat ${name}`}
            aria-pressed={chat.isFavorite === true}
            disabled={starring}
            onClick={onFavorite}
          >
            {chat.isFavorite ? "★" : "☆"}
          </button>
          <button
            type="button"
            className={`chat-history-open${chat.hasUnread ? " chat-history-unread" : ""}`}
            aria-current={current ? "true" : undefined}
            onClick={onOpen}
          >
            <span className="chat-history-title">
              <span className="chat-history-name">{name}</span>
              {chat.hasUnread && <UnreadDot />}
              {chat.needsYou && (
                <span className="chat-needs-you" role="img" aria-label="Needs you" />
              )}
            </span>
            <span className="chat-history-date" title={new Date(lastActivity).toLocaleString()}>
              {formatRelativeTime(lastActivity, now)}
            </span>
          </button>
          <button
            type="button"
            className="chat-link-btn"
            aria-label={`Rename chat ${name}`}
            onClick={begin}
          >
            ✎
          </button>
          <button
            type="button"
            className="chat-link-btn chat-danger"
            aria-label={`Delete chat ${name}`}
            onClick={onDelete}
          >
            ✕
          </button>
        </>
      )}
    </li>
  );
}

/**
 * The user's chats beside (or, in a narrow panel, instead of) the open chat:
 * starred ones first, each group in the history's last-activity order, filtered
 * by the search box. Its search text goes when it closes.
 */
export default function ChatSidebar({
  history,
  currentChatId,
  full,
  onOpen,
  onNewChat,
  onDelete,
  onDeleteAll,
  renameChat,
  renamesInFlight,
  onFavorite,
  favoritesInFlight,
}: {
  history: ChatSessionSummary[];
  currentChatId: string | null;
  /** Fills the panel rather than sitting beside the chat. */
  full: boolean;
  onOpen: (chatSessionId: string) => void;
  onNewChat: () => void;
  onDelete: (chatSessionId: string) => void;
  onDeleteAll: () => void;
  renameChat: RenameChat;
  renamesInFlight: ReadonlySet<string>;
  onFavorite: (chatSessionId: string, favorite: boolean) => void;
  favoritesInFlight: ReadonlySet<string>;
}) {
  const [confirmDeleteAll, setConfirmDeleteAll] = useState(false);
  const [query, setQuery] = useState("");
  // The chats whose messages match, and the query they answer: a match for any
  // other query is not applied.
  const [contentMatch, setContentMatch] = useState<{ query: string; ids: Set<string> } | null>(
    null,
  );
  const [now, setNow] = useState(Date.now);
  const term = query.trim();

  useEffect(() => {
    const tick = setInterval(() => setNow(Date.now()), CLOCK_TICK_MS);
    return () => clearInterval(tick);
  }, []);

  useEffect(() => {
    if (!term) return;
    let cancelled = false;
    const timer = setTimeout(() => {
      chatService.searchChats(term).then(
        (ids) => {
          if (!cancelled) setContentMatch({ query: term, ids: new Set(ids) });
        },
        // The title matches stay on screen; there is nothing more to show.
        (err) => console.error(err),
      );
    }, SEARCH_DEBOUNCE_MS);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [term]);

  const needle = term.toLowerCase();
  const contentIds = contentMatch?.query === term ? contentMatch.ids : null;
  const matching = term
    ? history.filter(
        (c) => shownName(c).toLowerCase().includes(needle) || contentIds?.has(c.id) === true,
      )
    : history;
  const rows = [...matching.filter((c) => c.isFavorite), ...matching.filter((c) => !c.isFavorite)];

  return (
    <aside className={`chat-sidebar${full ? " chat-sidebar-full" : ""}`} aria-label="Chats">
      <button type="button" className="chat-primary-btn" onClick={onNewChat}>
        New chat
      </button>
      <input
        className="chat-input chat-sidebar-search"
        type="search"
        placeholder="Search chats…"
        aria-label="Search chats"
        value={query}
        onChange={(e) => setQuery(e.target.value)}
      />
      {history.length > 0 && (
        <div className="chat-history-head">
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
                onClick={() => {
                  setConfirmDeleteAll(false);
                  onDeleteAll();
                }}
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
      )}
      {term && rows.length === 0 ? (
        <p className="chat-muted">No chats match</p>
      ) : (
        <ul className="chat-history-list">
          {rows.map((c) => (
            <ChatSessionRow
              key={c.id}
              chat={c}
              current={c.id === currentChatId}
              now={now}
              onOpen={() => onOpen(c.id)}
              onDelete={() => onDelete(c.id)}
              renameChat={renameChat}
              renaming={renamesInFlight.has(c.id)}
              onFavorite={() => onFavorite(c.id, !c.isFavorite)}
              starring={favoritesInFlight.has(c.id)}
            />
          ))}
        </ul>
      )}
    </aside>
  );
}
