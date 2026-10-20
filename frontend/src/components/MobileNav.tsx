import { useCallback, useState } from "react";
import { NavLink, useLocation } from "react-router";
import { useAuth } from "../hooks/useAuth";
import { useNeedsYouCount } from "../hooks/useNeedsYouCount";
import { usePendingGlobalVariableProposalCount } from "../hooks/usePendingGlobalVariableProposalCount";
import { ROUTES } from "../utils/constants";
import { useChatInbox } from "./ChatInbox";
import Icon, { type IconName } from "./Icon";
import Sheet from "./Sheet";
import "./MobileNav.css";

/** The four places a phone reaches in one tap; everything else is under More. */
const TABS: { label: string; path: string; icon: IconName }[] = [
  { label: "Taskboard", path: ROUTES.TASKBOARD, icon: "board" },
  { label: "Loops", path: ROUTES.LOOP_EDITOR, icon: "loop" },
  { label: "Analytics", path: ROUTES.ANALYTICS, icon: "chart" },
  { label: "Chat", path: ROUTES.CHAT, icon: "chat" },
];

const MORE: { label: string; path: string; icon: IconName }[] = [
  { label: "Settings", path: ROUTES.SETTINGS, icon: "gear" },
  { label: "Repositories", path: ROUTES.REPOSITORIES, icon: "repo" },
  { label: "AI Providers", path: ROUTES.AI_PROVIDERS, icon: "chat" },
  { label: "Global Variables", path: ROUTES.GLOBAL_VARIABLES, icon: "gear" },
];

/** The app menu as a sheet: the pages not on the tab bar, who is signed in, and Log out. */
export function MenuSheet({ onClose }: { onClose: () => void }) {
  const { user, logout } = useAuth();
  const pendingSuggestions = usePendingGlobalVariableProposalCount();
  return (
    <Sheet title="Menu" onClose={onClose}>
      <nav aria-label="More pages">
        {MORE.map((item) => (
          <NavLink key={item.path} to={item.path} className="sheet-row" onClick={onClose}>
            <Icon name={item.icon} size={18} />
            {item.label}
            {item.path === ROUTES.GLOBAL_VARIABLES && pendingSuggestions > 0 && (
              <span className="sheet-row-badge">
                {pendingSuggestions}
                <span className="sr-only"> suggestions waiting</span>
              </span>
            )}
          </NavLink>
        ))}
      </nav>
      <hr className="sheet-rule" />
      {user?.username && (
        <div className="sheet-row sheet-row-static">
          <Icon name="user" size={18} />
          {user.username}
        </div>
      )}
      <button
        type="button"
        className="sheet-row"
        onClick={() => {
          onClose();
          void logout();
        }}
      >
        <Icon name="logout" size={18} />
        Log out
      </button>
    </Sheet>
  );
}

/**
 * The phone's main navigation, fixed to the bottom of the screen. Taskboard
 * carries how many items need you; Chat how many chats are unread. More opens
 * the menu with the rest of the pages and Log out.
 */
export default function MobileTabBar() {
  const { pathname } = useLocation();
  const needsYou = useNeedsYouCount();
  const { unreadCount } = useChatInbox();
  const [menuOpen, setMenuOpen] = useState(false);
  const closeMenu = useCallback(() => setMenuOpen(false), []);
  const onMorePage = MORE.some((item) => pathname.startsWith(item.path));

  return (
    <>
      <nav className="tabbar" aria-label="Main">
        {TABS.map((tab) => {
          const count =
            tab.path === ROUTES.TASKBOARD
              ? needsYou
              : tab.path === ROUTES.CHAT
                ? unreadCount
                : null;
          return (
            <NavLink
              key={tab.path}
              to={tab.path}
              className={({ isActive }) => `tabbar-item${isActive ? " is-active" : ""}`}
            >
              <Icon name={tab.icon} size={20} />
              {tab.label}
              {count !== null && count > 0 && (
                <span className="tabbar-badge">
                  {count}
                  <span className="sr-only">
                    {tab.path === ROUTES.TASKBOARD ? " need you" : " unread"}
                  </span>
                </span>
              )}
            </NavLink>
          );
        })}
        <button
          type="button"
          className={`tabbar-item${onMorePage ? " is-active" : ""}`}
          aria-haspopup="dialog"
          aria-expanded={menuOpen}
          onClick={() => setMenuOpen(true)}
        >
          <Icon name="menu" size={20} />
          More
        </button>
      </nav>
      {menuOpen && <MenuSheet onClose={closeMenu} />}
    </>
  );
}
