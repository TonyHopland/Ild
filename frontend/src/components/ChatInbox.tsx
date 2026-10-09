import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { useSignalR } from "../hooks/useSignalR";
import { chatService } from "../services/auth";
import type { ChatSessionSummary } from "../types";
import type { RenameChat } from "./ChatRename";

/**
 * The chats with a request of one kind out, one claim per chat: a second request
 * for a chat already claimed is refused. The ref is the synchronous check; the
 * state disables the control.
 */
function useChatClaims() {
  const claimsRef = useRef(new Set<string>());
  const [claimed, setClaimed] = useState<ReadonlySet<string>>(new Set());
  const claim = useCallback((chatSessionId: string) => {
    if (claimsRef.current.has(chatSessionId)) return false;
    claimsRef.current.add(chatSessionId);
    setClaimed(new Set(claimsRef.current));
    return true;
  }, []);
  const release = useCallback((chatSessionId: string) => {
    claimsRef.current.delete(chatSessionId);
    setClaimed(new Set(claimsRef.current));
  }, []);
  return { claimed, claim, release };
}

export interface ChatInbox {
  /** The user's retained chats (ADR-0013), in the server's order: newest activity first. */
  history: ChatSessionSummary[];
  /** Whether the first history read has come back, answered or not. */
  loaded: boolean;
  /** How many chats hold a reply the user has not read. */
  unreadCount: number;
  refreshHistory: () => Promise<void>;
  /** Reports the chat read up to `newest`, once per sequence. */
  markChatRead: (chatSessionId: string, newest: number) => void;
  deleteChat: (chatSessionId: string) => Promise<void>;
  deleteAllChats: () => Promise<void>;
  renameChat: RenameChat;
  renamesInFlight: ReadonlySet<string>;
  setFavorite: (chatSessionId: string, favorite: boolean) => Promise<void>;
  favoritesInFlight: ReadonlySet<string>;
  /**
   * The chat open in whichever frame shows chat — the bubble or the Chat tab — so
   * the other opens the same one. Null when none is.
   */
  activeChatId: string | null;
  setActiveChatId: (chatSessionId: string | null) => void;
}

const noClaims: ReadonlySet<string> = new Set();

const ChatInboxContext = createContext<ChatInbox>({
  history: [],
  loaded: false,
  unreadCount: 0,
  refreshHistory: () => Promise.resolve(),
  markChatRead: () => {},
  deleteChat: () => Promise.resolve(),
  deleteAllChats: () => Promise.resolve(),
  renameChat: () => Promise.resolve(false),
  renamesInFlight: noClaims,
  setFavorite: () => Promise.resolve(),
  favoritesInFlight: noClaims,
  activeChatId: null,
  setActiveChatId: () => {},
});

export function useChatInbox(): ChatInbox {
  return useContext(ChatInboxContext);
}

/**
 * The signed-in user's chat list, its unread state and their inbox subscription:
 * one for the whole app, so the header, the bubble and the Chat tab show the same
 * list and the inbox is joined once.
 */
export function ChatInboxProvider({ children }: { children: React.ReactNode }) {
  const [history, setHistory] = useState<ChatSessionSummary[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [activeChatId, setActiveChatId] = useState<string | null>(null);
  // Per chat, not per draft: a draft cancelled mid-save leaves its request running.
  const { claimed: renamesInFlight, claim: claimRename, release: releaseRename } = useChatClaims();
  const {
    claimed: favoritesInFlight,
    claim: claimFavorite,
    release: releaseFavorite,
  } = useChatClaims();

  const { connectionState, on, off, invoke } = useSignalR("/hubs/chat");

  // History reads are ordered the same way as a chat's state reads: an answer
  // lands only if no newer read has landed first. On top of that, a read taken
  // before a local change to the list — a delete, or a mark-read the server has
  // confirmed — is a snapshot of a list that no longer exists, and would bring
  // back the chat or the dot that change removed. So such a read is dropped, and a
  // fresh one goes out in its place: whatever the dropped read was sent to pick
  // up, a reply in another chat say, still arrives.
  const historyReadRef = useRef(0);
  const appliedHistoryReadRef = useRef(0);
  const historyEpochRef = useRef(0);

  const refreshHistory = useCallback(async function readHistory(): Promise<void> {
    const read = ++historyReadRef.current;
    const epoch = historyEpochRef.current;
    const chats = await chatService.listHistory();
    if (read <= appliedHistoryReadRef.current) return;
    if (historyEpochRef.current !== epoch) return readHistory();
    appliedHistoryReadRef.current = read;
    setHistory(chats);
  }, []);

  // Load the chat history once, so the list is ready when chat is first shown.
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

  // Join this user's inbox, which hears when any of their chats may have turned
  // unread or read, and re-read the history once joined. On every connect: the
  // server drops the membership with the connection, and a hint sent while it was
  // down is lost, so only the re-read can recover it.
  useEffect(() => {
    if (connectionState !== "connected") return;
    void (async () => {
      try {
        await invoke("SubscribeToChatInbox");
        await refreshHistory();
      } catch (err) {
        console.error(err);
      }
    })();
    return () => {
      void invoke("UnsubscribeFromChatInbox")?.catch((err) => console.error(err));
    };
  }, [connectionState, invoke, refreshHistory]);

  // Every hint means the same thing here: re-read the history, which carries every
  // chat's unread flag, busy flag, title and schedule mark — the open chat's header
  // included. A schedule renamed or deleted changes the mark of every chat it started
  // without a word about any one chat, so its hint re-reads the history too.
  useEffect(() => {
    const onHistoryChanged = () => {
      void refreshHistory().catch((err) => console.error(err));
    };
    on("ChatUnreadChanged", onHistoryChanged);
    on("ChatTitleChanged", onHistoryChanged);
    on("ChatActivityChanged", onHistoryChanged);
    on("ChatSchedulesChanged", onHistoryChanged);
    return () => {
      off("ChatUnreadChanged", onHistoryChanged);
      off("ChatTitleChanged", onHistoryChanged);
      off("ChatActivityChanged", onHistoryChanged);
      off("ChatSchedulesChanged", onHistoryChanged);
    };
  }, [on, off, refreshHistory]);

  // The newest sequence each chat has been marked read up to, or has a request
  // out for, so every message is reported once. A request that fails gives its
  // claim back, provided nothing newer has taken it over, and the next time the
  // chat is on screen asks again.
  const markedRef = useRef(new Map<string, number>());

  const markChatRead = useCallback(
    (id: string, newest: number) => {
      if (newest <= (markedRef.current.get(id) ?? -1)) return;
      markedRef.current.set(id, newest);
      void (async () => {
        try {
          await chatService.markRead(id, newest);
        } catch (err) {
          // Nothing to show the user: the chat simply stays unread on the server.
          if (markedRef.current.get(id) === newest) markedRef.current.delete(id);
          console.error(err);
          return;
        }
        historyEpochRef.current += 1;
        await refreshHistory().catch((err) => console.error(err));
      })();
    },
    [refreshHistory],
  );

  const deleteChat = useCallback(async (id: string) => {
    setActiveChatId((active) => (active === id ? null : active));
    try {
      await chatService.deleteOne(id);
    } catch {
      /* even on error, drop it locally — it is gone or never existed */
    }
    historyEpochRef.current += 1;
    setHistory((prev) => prev.filter((c) => c.id !== id));
  }, []);

  const deleteAllChats = useCallback(async () => {
    setActiveChatId(null);
    try {
      await chatService.deleteAll();
    } catch {
      /* even on error, clear the list — the chats are gone or never existed */
    }
    historyEpochRef.current += 1;
    setHistory([]);
  }, []);

  // A rename the server took is shown at once, like a delete; the re-read that
  // follows only reconciles, so one that fails or stalls cannot hold the form open
  // or leave the old title on screen.
  const renameChat = useCallback<RenameChat>(
    async (chatSessionId, name) => {
      if (!claimRename(chatSessionId)) return false;
      try {
        await chatService.rename(chatSessionId, name);
      } finally {
        releaseRename(chatSessionId);
      }
      historyEpochRef.current += 1;
      setHistory((rows) => rows.map((c) => (c.id === chatSessionId ? { ...c, name } : c)));
      void refreshHistory().catch((err) => console.error(err));
      return true;
    },
    [claimRename, releaseRename, refreshHistory],
  );

  // A star the server took is shown at once, like a rename; a failure leaves the
  // star as it was.
  const setFavorite = useCallback(
    async (chatSessionId: string, favorite: boolean) => {
      if (!claimFavorite(chatSessionId)) return;
      try {
        await chatService.setFavorite(chatSessionId, favorite);
      } catch (err) {
        console.error(err);
        return;
      } finally {
        releaseFavorite(chatSessionId);
      }
      historyEpochRef.current += 1;
      setHistory((rows) =>
        rows.map((c) => (c.id === chatSessionId ? { ...c, isFavorite: favorite } : c)),
      );
      void refreshHistory().catch((err) => console.error(err));
    },
    [claimFavorite, releaseFavorite, refreshHistory],
  );

  const unreadCount = history.filter((c) => c.hasUnread).length;

  const inbox = useMemo<ChatInbox>(
    () => ({
      history,
      loaded,
      unreadCount,
      refreshHistory,
      markChatRead,
      deleteChat,
      deleteAllChats,
      renameChat,
      renamesInFlight,
      setFavorite,
      favoritesInFlight,
      activeChatId,
      setActiveChatId,
    }),
    [
      history,
      loaded,
      unreadCount,
      refreshHistory,
      markChatRead,
      deleteChat,
      deleteAllChats,
      renameChat,
      renamesInFlight,
      setFavorite,
      favoritesInFlight,
      activeChatId,
    ],
  );

  return <ChatInboxContext.Provider value={inbox}>{children}</ChatInboxContext.Provider>;
}
