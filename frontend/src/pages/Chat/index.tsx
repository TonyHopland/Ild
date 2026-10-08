import { useCallback, useEffect } from "react";
import { NavLink, useLocation, useNavigate, useParams } from "react-router";
import { useChatInbox } from "../../components/ChatInbox";
import ChatWorkspace from "../../components/ChatWorkspace";
import { ROUTES } from "../../utils/constants";
import ChatSchedules from "./ChatSchedules";
import "./Chat.css";

/** Marks a visit to the Chat tab that shows the start form rather than a chat. */
const START_FORM = { startForm: true } as const;

const chatPath = (chatSessionId: string) => `${ROUTES.CHAT}/${chatSessionId}`;

/**
 * Chat full screen, laid out like an inbox: the chat list beside the open chat,
 * or beside the user's chat schedules. The chat is the one in the URL. A message
 * sent from here carries no Chat Context (ADR-0011): nothing is open beside it
 * for the agent to see.
 */
export default function Chat({ view = "chat" }: { view?: "chat" | "schedules" }) {
  const { chatId } = useParams();
  const location = useLocation();
  const navigate = useNavigate();
  const { history, loaded, activeChatId } = useChatInbox();
  const schedules = view === "schedules";
  const startForm = (location.state as { startForm?: boolean } | null)?.startForm === true;

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
    if (landing === null) void navigate(ROUTES.CHAT, { replace: true, state: START_FORM });
    else void navigate(chatPath(landing), { replace: true });
  }, [landing, navigate]);

  const onActiveChatChange = useCallback(
    (id: string | null) => {
      if (id === null) {
        if (chatId !== undefined) void navigate(ROUTES.CHAT, { state: START_FORM });
      } else if (id !== chatId) {
        void navigate(chatPath(id));
      }
    },
    [chatId, navigate],
  );

  const openChat = useCallback(
    (id: string) => {
      if (id !== chatId) void navigate(chatPath(id));
    },
    [chatId, navigate],
  );

  // Beside the schedules no chat is open, so New chat goes to the start form.
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
      onActiveChatChange={onActiveChatChange}
      onChatShown={schedules ? showStartForm : undefined}
      onOpenChat={openChat}
      sidebarExtras={
        <NavLink to={ROUTES.CHAT_SCHEDULES} className="chat-sidebar-link">
          Schedules
        </NavLink>
      }
      showScheduleMarks
    >
      {({ title, error, sidebar, content }) => (
        <div className="chat-page">
          {loaded ? (
            <>
              {sidebar}
              {schedules ? (
                <ChatSchedules />
              ) : (
                <section className="chat-page-chat">
                  <header className="chat-page-header">
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
