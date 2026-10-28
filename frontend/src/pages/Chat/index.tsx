import { useCallback, useEffect, useRef, useState } from "react";
import { NavLink, useLocation, useNavigate, useParams, useSearchParams } from "react-router";
import { useChatInbox } from "../../components/ChatInbox";
import ChatWorkspace from "../../components/ChatWorkspace";
import { ROUTES } from "../../utils/constants";
import { useIsMobile } from "../../hooks/useIsMobile";
import ChatsBackButton from "./ChatsBackButton";
import Schedules from "./Schedules";
import "./Chat.css";

/** Marks a visit to the Chat tab that shows the start form rather than a chat. */
const START_FORM = { startForm: true } as const;

/**
 * Marks a visit that arrived at the Chat tab itself rather than at one chat: a
 * phone, with room for the list or a chat but not both, shows it the list.
 */
const FROM_TAB = { fromTab: true } as const;

const chatPath = (chatSessionId: string) => `${ROUTES.CHAT}/${chatSessionId}`;

/**
 * Chat full screen, laid out like an inbox: the chat list beside the open chat, or
 * beside the user's chat schedules. The chat is the one in the URL, scrolled to the
 * global variable suggestion its `globalProposal` parameter names. A message sent
 * from here carries no Chat Context (ADR-0011): nothing is open beside it for the
 * agent to see.
 */
export default function Chat({ view = "chat" }: { view?: "chat" | "schedules" }) {
  const { chatId } = useParams();
  const [searchParams] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const { history, loaded, activeChatId } = useChatInbox();
  const schedules = view === "schedules";
  const visit = location.state as { startForm?: boolean; fromTab?: boolean } | null;
  const startForm = visit?.startForm === true;
  // A phone shows the chat list or the open chat, one at a time. The list shows
  // for a visit to the tab itself, and when asked for with Chats; picking a chat
  // from it, or starting one, shows that chat.
  const isMobile = useIsMobile();
  const [listShown, setListShown] = useState(visit?.fromTab === true);
  const shownChatRef = useRef(chatId);
  useEffect(() => {
    if (shownChatRef.current === chatId) return;
    shownChatRef.current = chatId;
    setListShown(false);
  }, [chatId]);
  // The same page stays mounted as /chat lands on a chat, so the visit is read as
  // it arrives rather than only when the page first renders.
  useEffect(() => {
    if (visit?.fromTab === true) setListShown(true);
  }, [location.key]);
  const showList = useCallback(() => setListShown(true), []);

  // Which chat a visit to /chat lands on: the one open in the bubble, else the one
  // with the newest activity, else the start form. Decided once per visit: the
  // start form is written into the visit, so a chat arriving later does not take
  // its place.
  const landing =
    chatId !== undefined || startForm || schedules
      ? undefined
      : (activeChatId ?? (loaded ? (history[0]?.id ?? null) : undefined));
  useEffect(() => {
    if (landing === undefined) return;
    if (landing === null)
      void navigate(ROUTES.CHAT, { replace: true, state: { ...START_FORM, ...FROM_TAB } });
    else void navigate(chatPath(landing), { replace: true, state: FROM_TAB });
  }, [landing, navigate]);

  const onActiveChatChange = useCallback(
    (id: string | null) => {
      // Reported for the chat this page opened too, so only leaving a chat for
      // the start form (New chat) counts as a pick that closes the list.
      if (id === null) {
        setListShown(false);
        if (chatId !== undefined) void navigate(ROUTES.CHAT, { state: START_FORM });
      } else if (id !== chatId) {
        void navigate(chatPath(id));
      }
    },
    [chatId, navigate],
  );

  const openChat = useCallback(
    (id: string) => {
      setListShown(false);
      if (id !== chatId) void navigate(chatPath(id));
    },
    [chatId, navigate],
  );

  // Beside the schedules, a pick from the list goes to the chat and New chat goes
  // to the start form; nothing is opened in place.
  const showStartForm = useCallback(
    () => void navigate(ROUTES.CHAT, { state: START_FORM }),
    [navigate],
  );

  if (chatId === undefined && !startForm && !schedules) return <div className="chat-page" />;

  return (
    <ChatWorkspace
      key={schedules ? "schedules" : (chatId ?? "new")}
      initialChatId={chatId ?? null}
      routeContext={false}
      shown={!schedules}
      covered={false}
      sidebarShown
      onActiveChatChange={schedules ? undefined : onActiveChatChange}
      routesStartedChat
      onChatShown={schedules ? showStartForm : undefined}
      onOpenChat={openChat}
      sidebarExtras={
        <NavLink to={ROUTES.CHAT_SCHEDULES} className="chat-sidebar-link">
          Schedules
        </NavLink>
      }
      scheduleMarks
      revealGlobalProposalId={schedules ? null : searchParams.get("globalProposal")}
    >
      {({ title, error, sidebar, content }) => (
        <div className={`chat-page${isMobile && listShown ? " chat-page--list" : ""}`}>
          {loaded ? (
            <>
              {sidebar}
              {schedules ? (
                <Schedules onShowList={isMobile ? showList : undefined} />
              ) : (
                <section className="chat-page-chat">
                  <header className="chat-page-header">
                    {isMobile && <ChatsBackButton onClick={showList} />}
                    <h1 className="chat-page-title">{title ?? "AI Chat"}</h1>
                  </header>
                  {error && <div className="chat-error">{error}</div>}
                  {content}
                </section>
              )}
            </>
          ) : (
            <div className="chat-panel-body chat-muted">Loading…</div>
          )}
        </div>
      )}
    </ChatWorkspace>
  );
}
