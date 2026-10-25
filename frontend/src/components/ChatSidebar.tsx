import { useCallback, useEffect, useRef, useState } from "react";
import { chatService } from "../services/auth";
import type { ChatSessionSummary } from "../types";
import { formatRelativeTime } from "../utils/relativeTime";
import Icon from "./Icon";
import { RenameForm, UnreadDot, useTitleEdit, type RenameChat } from "./ChatRename";

const SEARCH_DEBOUNCE_MS = 300;
const CLOCK_TICK_MS = 60_000;

const shownName = (chat: ChatSessionSummary) => chat.name ?? "Untitled chat";

/** One chat in the list, renamable in place. Keyed by the chat. */
function ChatSessionRow({
  chat,
  current,
  full,
  now,
  menuOpen,
  onMenu,
  onOpen,
  onDelete,
  renameChat,
  renaming,
  onFavorite,
  starring,
  scheduleMark,
  selecting,
  checked,
  onToggle,
}: {
  chat: ChatSessionSummary;
  current: boolean;
  /** The list fills the frame, so the open chat's own working indicator is out of sight. */
  full: boolean;
  now: number;
  /** Whether this row's actions are showing; one row's at a time. */
  menuOpen: boolean;
  onMenu: (chatSessionId: string, open: boolean) => void;
  onOpen: () => void;
  onDelete: () => void;
  renameChat: RenameChat;
  renaming: boolean;
  onFavorite: () => void;
  starring: boolean;
  /** Mark it when a chat schedule started it. */
  scheduleMark: boolean;
  /** Selection mode: the row is a checkbox at its end, with no actions menu. */
  selecting: boolean;
  checked: boolean;
  onToggle: () => void;
}) {
  const { editing, begin, closer } = useTitleEdit();
  const actionsRef = useRef<HTMLDivElement | null>(null);
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const menuRef = useRef<HTMLDivElement | null>(null);
  const name = shownName(chat);
  const lastActivity = chat.updatedAt ?? chat.createdAt;

  // The list scrolls and clips, so a menu opened on a row near its bottom edge
  // would hang out of sight below it. `scrollIntoView` is absent in jsdom.
  useEffect(() => {
    if (menuOpen) menuRef.current?.scrollIntoView?.({ block: "nearest" });
  }, [menuOpen]);

  useEffect(() => {
    if (!menuOpen) return;
    const onPointerDown = (e: PointerEvent) => {
      if (!actionsRef.current?.contains(e.target as Node)) onMenu(chat.id, false);
    };
    document.addEventListener("pointerdown", onPointerDown);
    return () => document.removeEventListener("pointerdown", onPointerDown);
  }, [menuOpen, onMenu, chat.id]);

  const choose = (action: () => void) => () => {
    onMenu(chat.id, false);
    action();
  };

  const content = (
    <>
      <span className="chat-history-title">
        {chat.isFavorite && (
          <span className="chat-starred" role="img" aria-label="Starred">
            ★
          </span>
        )}
        <span className="chat-history-name">{name}</span>
        {scheduleMark && chat.scheduleName && (
          <span
            className="chat-scheduled"
            role="img"
            aria-label={`Started by the schedule ${chat.scheduleName}`}
            title={`Started by the schedule ${chat.scheduleName}`}
          >
            <svg viewBox="0 0 16 16" width="11" height="11" aria-hidden="true">
              <circle cx="8" cy="8" r="6.5" fill="none" stroke="currentColor" strokeWidth="1.5" />
              <path d="M8 4.5V8l2.5 1.5" fill="none" stroke="currentColor" strokeWidth="1.5" />
            </svg>
          </span>
        )}
        {chat.hasUnread && <UnreadDot />}
        {chat.isBusy && (!current || full) && (
          <span className="chat-busy" role="img" aria-label="Working" />
        )}
        {chat.needsYou && <span className="chat-needs-you" role="img" aria-label="Needs you" />}
      </span>
      <span className="chat-history-date" title={new Date(lastActivity).toLocaleString()}>
        {formatRelativeTime(lastActivity, now)}
      </span>
    </>
  );

  const openClass = `chat-history-open${chat.hasUnread ? " chat-history-unread" : ""}`;

  if (selecting) {
    return (
      <li className="chat-history-row">
        <label
          className={`${openClass} chat-history-select`}
          aria-current={current ? "true" : undefined}
        >
          <span className="chat-history-text">{content}</span>
          <input
            type="checkbox"
            aria-label={`Select chat ${name}`}
            checked={checked}
            onChange={onToggle}
          />
        </label>
      </li>
    );
  }

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
            className={openClass}
            aria-current={current ? "true" : undefined}
            onClick={onOpen}
          >
            {content}
          </button>
          <div
            className="chat-row-actions"
            ref={actionsRef}
            onKeyDown={(e) => {
              if (e.key !== "Escape" || !menuOpen) return;
              // A page under the chat, such as an open work item, closes on Escape too.
              e.stopPropagation();
              onMenu(chat.id, false);
              triggerRef.current?.focus();
            }}
          >
            <button
              type="button"
              ref={triggerRef}
              className="chat-link-btn chat-row-menu-btn"
              aria-label={`Actions for chat ${name}`}
              aria-expanded={menuOpen}
              onClick={() => onMenu(chat.id, !menuOpen)}
            >
              <Icon name="dots" />
            </button>
            {menuOpen && (
              <div
                className="chat-row-menu"
                ref={menuRef}
                role="group"
                aria-label={`Actions for chat ${name}`}
              >
                <button
                  type="button"
                  className="chat-row-menu-item"
                  aria-label={`Favorite chat ${name}`}
                  aria-pressed={chat.isFavorite === true}
                  disabled={starring}
                  onClick={choose(() => {
                    triggerRef.current?.focus();
                    onFavorite();
                  })}
                >
                  {chat.isFavorite ? "★ Unfavorite" : "☆ Favorite"}
                </button>
                <button
                  type="button"
                  className="chat-row-menu-item"
                  aria-label={`Rename chat ${name}`}
                  onClick={choose(begin)}
                >
                  ✎ Rename
                </button>
                <button
                  type="button"
                  className="chat-row-menu-item chat-danger"
                  aria-label={`Delete chat ${name}`}
                  onClick={choose(onDelete)}
                >
                  ✕ Delete
                </button>
              </div>
            )}
          </div>
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
  onDeleteMany,
  renameChat,
  renamesInFlight,
  onFavorite,
  favoritesInFlight,
  extras,
  scheduleMarks = false,
}: {
  history: ChatSessionSummary[];
  currentChatId: string | null;
  /** Fills the panel rather than sitting beside the chat. */
  full: boolean;
  onOpen: (chatSessionId: string) => void;
  onNewChat: () => void;
  onDelete: (chatSessionId: string) => void;
  onDeleteMany: (chatSessionIds: string[]) => void;
  renameChat: RenameChat;
  renamesInFlight: ReadonlySet<string>;
  onFavorite: (chatSessionId: string, favorite: boolean) => void;
  favoritesInFlight: ReadonlySet<string>;
  /** Entries a frame adds below New chat, which another frame's list does not show. */
  extras?: React.ReactNode;
  /** Whether chats a schedule started carry a mark naming it; the bubble's list has no room for one. */
  scheduleMarks?: boolean;
}) {
  const [selecting, setSelecting] = useState(false);
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  // The ids the open confirmation asks about, fixed when it opened.
  const [confirmDelete, setConfirmDelete] = useState<string[] | null>(null);
  const selectAllRef = useRef<HTMLInputElement | null>(null);
  const [query, setQuery] = useState("");
  // The answer of the message search and the query it answers: the matching chats,
  // or null when the search failed. An answer for any other query is not applied.
  const [contentSearch, setContentSearch] = useState<{
    query: string;
    ids: Set<string> | null;
  } | null>(null);
  const [now, setNow] = useState(Date.now);
  const term = query.trim();
  // The row whose actions are showing. A row closes only its own, so a close that
  // lands after another row has opened leaves that one alone.
  const [menuFor, setMenuFor] = useState<string | null>(null);
  const onMenu = useCallback((chatSessionId: string, open: boolean) => {
    setMenuFor((shown) => (open ? chatSessionId : shown === chatSessionId ? null : shown));
  }, []);

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
          if (!cancelled) setContentSearch({ query: term, ids: new Set(ids) });
        },
        (err) => {
          // The title matches stay on screen; with none, the failure is shown.
          console.error(err);
          if (!cancelled) setContentSearch({ query: term, ids: null });
        },
      );
    }, SEARCH_DEBOUNCE_MS);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [term]);

  const needle = term.toLowerCase();
  const answer = contentSearch?.query === term ? contentSearch : null;
  const contentIds = answer?.ids ?? null;
  const matching = term
    ? history.filter(
        (c) => shownName(c).toLowerCase().includes(needle) || contentIds?.has(c.id) === true,
      )
    : history;
  const rows = [...matching.filter((c) => c.isFavorite), ...matching.filter((c) => !c.isFavorite)];

  // Only the listed rows count: a check on a row the search hides is kept, never sent.
  const checkedIds = rows.filter((c) => selected.has(c.id)).map((c) => c.id);
  const allChecked = rows.length > 0 && checkedIds.length === rows.length;
  const someChecked = checkedIds.length > 0 && !allChecked;
  const count = checkedIds.length;

  const exitSelecting = () => {
    setSelecting(false);
    setSelected(new Set());
    setConfirmDelete(null);
  };
  if (selecting && history.length === 0) exitSelecting();

  // Changing the checks or the search while the confirmation is open withdraws it, so
  // what it asks about is always what the screen shows checked.
  const toggle = (id: string) => {
    setConfirmDelete(null);
    setSelected((prev) => {
      const next = new Set(prev);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  };
  const toggleAll = () =>
    setSelected((prev) => {
      const next = new Set(prev);
      for (const c of rows) {
        if (allChecked) next.delete(c.id);
        else next.add(c.id);
      }
      return next;
    });

  useEffect(() => {
    if (selectAllRef.current) selectAllRef.current.indeterminate = someChecked;
  }, [someChecked, selecting, confirmDelete]);

  return (
    <aside className={`chat-sidebar${full ? " chat-sidebar-full" : ""}`} aria-label="Chats">
      <button type="button" className="btn btn-primary" onClick={onNewChat}>
        New chat
      </button>
      {extras}
      <input
        className="chat-input chat-sidebar-search"
        type="search"
        placeholder="Search chats…"
        aria-label="Search chats"
        value={query}
        onChange={(e) => {
          setQuery(e.target.value);
          setConfirmDelete(null);
        }}
      />
      {history.length > 0 && (
        <div className="chat-history-head">
          {!selecting ? (
            <button
              type="button"
              className="chat-link-btn"
              onClick={() => {
                setSelecting(true);
                setMenuFor(null);
              }}
            >
              Select
            </button>
          ) : confirmDelete ? (
            <span
              className="chat-history-confirm"
              role="alertdialog"
              aria-label={`Delete ${confirmDelete.length} ${confirmDelete.length === 1 ? "chat" : "chats"}?`}
            >
              Delete {confirmDelete.length} {confirmDelete.length === 1 ? "chat" : "chats"}? This
              can't be undone.
              <button
                type="button"
                className="chat-link-btn chat-danger"
                onClick={() => {
                  exitSelecting();
                  onDeleteMany(confirmDelete);
                }}
              >
                Delete
              </button>
              <button
                type="button"
                className="chat-link-btn"
                onClick={() => setConfirmDelete(null)}
              >
                Cancel
              </button>
            </span>
          ) : (
            <>
              <label className="chat-history-select-all">
                <input
                  type="checkbox"
                  ref={selectAllRef}
                  aria-label="Select all"
                  checked={allChecked}
                  disabled={rows.length === 0}
                  onChange={toggleAll}
                />
              </label>
              <button
                type="button"
                className="chat-link-btn chat-danger"
                disabled={count === 0}
                onClick={() => setConfirmDelete(checkedIds)}
              >
                Delete ({count})
              </button>
              <button type="button" className="chat-link-btn" onClick={exitSelecting}>
                Cancel
              </button>
            </>
          )}
        </div>
      )}
      {term && rows.length === 0 ? (
        <p className="chat-muted">
          {!answer ? "Searching…" : answer.ids ? "No chats match" : "Message search failed"}
        </p>
      ) : (
        <ul className="chat-history-list">
          {rows.map((c) => (
            <ChatSessionRow
              key={c.id}
              chat={c}
              current={c.id === currentChatId}
              full={full}
              now={now}
              menuOpen={menuFor === c.id}
              onMenu={onMenu}
              onOpen={() => onOpen(c.id)}
              onDelete={() => onDelete(c.id)}
              renameChat={renameChat}
              renaming={renamesInFlight.has(c.id)}
              onFavorite={() => onFavorite(c.id, !c.isFavorite)}
              starring={favoritesInFlight.has(c.id)}
              scheduleMark={scheduleMarks}
              selecting={selecting}
              checked={selected.has(c.id)}
              onToggle={() => toggle(c.id)}
            />
          ))}
        </ul>
      )}
    </aside>
  );
}
