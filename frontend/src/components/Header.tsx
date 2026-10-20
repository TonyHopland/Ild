import { useId } from "react";
import { NavLink } from "react-router";
import { useAuth } from "../hooks/useAuth";
import { NAV_ITEMS, APP_NAME, ROUTES } from "../utils/constants";
import { useChatInbox } from "./ChatInbox";
import { usePendingGlobalVariableProposalCount } from "../hooks/usePendingGlobalVariableProposalCount";
import SchedulerChip from "./SchedulerChip";

export default function Header() {
  const { user, logout } = useAuth();
  const { unreadCount } = useChatInbox();
  const unreadBadgeId = useId();
  const pendingSuggestions = usePendingGlobalVariableProposalCount();
  const suggestionsBadgeId = useId();

  return (
    <header className="header">
      <div className="header-content">
        <div className="header-brand">
          <NavLink to="/taskboard">{APP_NAME}</NavLink>
        </div>
        <nav className="header-nav">
          {NAV_ITEMS.map((item) => {
            const badge =
              item.path === ROUTES.CHAT && unreadCount > 0
                ? { id: unreadBadgeId, count: unreadCount }
                : item.path === ROUTES.GLOBAL_VARIABLES && pendingSuggestions > 0
                  ? { id: suggestionsBadgeId, count: pendingSuggestions }
                  : null;
            return (
              <NavLink
                key={item.path}
                to={item.path}
                className={({ isActive }) => (isActive ? "nav-link active" : "nav-link")}
                aria-describedby={badge?.id}
              >
                {item.label}
                {/* The link's name stays its label; the count reaches screen
                    readers as its description. */}
                {badge && (
                  <>
                    {" "}
                    <span id={badge.id} className="nav-badge" aria-hidden="true">
                      {badge.count}
                    </span>
                  </>
                )}
              </NavLink>
            );
          })}
        </nav>
        <div className="header-user">
          <SchedulerChip />
          <span className="user-name">{user?.username}</span>
          <button className="btn btn-logout" onClick={logout}>
            Logout
          </button>
        </div>
      </div>
      <style>{`
        .header {
          position: fixed;
          top: 0;
          left: 0;
          right: 0;
          z-index: 100;
          background-color: var(--surface);
          border-bottom: 1px solid var(--border);
          height: 4rem;
        }

        .header-content {
          display: flex;
          align-items: center;
          justify-content: space-between;
          gap: 1rem;
          width: 100%;
          padding: 0 1rem;
          height: 100%;
        }

        /* Brand and user block keep their size; the nav is the only part that
           gives way, so a narrow viewport scrolls the nav instead of squashing
           the username and Logout button. */
        .header-brand,
        .header-user {
          flex-shrink: 0;
        }

        .header-brand a {
          font-size: 1.25rem;
          font-weight: 700;
          color: #e0e0e0;
          text-decoration: none;
        }

        /* min-width: 0 lets this flex child shrink below its content width so the
           overflow can scroll; without it the nav stays at full width and pushes
           the last items off the viewport with no way to reach them. The
           scrollbar is hidden because it would sit across a 4rem-tall bar — the
           strip still scrolls by touch, wheel and keyboard focus. */
        .header-nav {
          display: flex;
          gap: 0.5rem;
          min-width: 0;
          overflow-x: auto;
          overscroll-behavior-x: contain;
          scrollbar-width: none;
        }

        .header-nav::-webkit-scrollbar {
          display: none;
        }

        .nav-link {
          flex-shrink: 0;
          white-space: nowrap;
          padding: 0.5rem 1rem;
          border-radius: 0.375rem;
          color: #a0a0b0;
          text-decoration: none;
          font-size: 0.875rem;
          transition: all 0.15s ease;
        }

        /* Drawn inside the link: a scroll container clips ink overflow at its
           padding box, and the nav has no padding, so a ring drawn outward
           would lose its top and bottom edges. */
        .nav-link:focus-visible {
          outline: 2px solid #6366f1;
          outline-offset: -2px;
        }

        .nav-link:hover {
          color: #e0e0e0;
          background-color: #2d2d44;
        }

        .nav-link.active {
          color: #fff;
          background-color: #3a3a5c;
        }

        .nav-badge {
          margin-left: 0.15rem;
          min-width: 1rem;
          padding: 0 0.3rem;
          border-radius: 0.5rem;
          background: #b91c1c;
          color: #fff;
          font-size: 0.7rem;
          font-weight: 600;
          line-height: 1rem;
          display: inline-block;
          text-align: center;
        }

        .header-user {
          display: flex;
          align-items: center;
          gap: 1rem;
        }

        .user-name {
          font-size: 0.875rem;
          color: #a0a0b0;
        }

        .scheduler-chip-wrap {
          position: relative;
          display: inline-flex;
        }

        .scheduler-chip {
          display: inline-flex;
          align-items: center;
          gap: 8px;
          height: 32px;
          padding: 0 12px;
          border-radius: 999px;
          border: 1px solid var(--border);
          background: var(--surface-2);
          color: var(--text-2);
          font-size: var(--fs-xs);
          cursor: pointer;
          white-space: nowrap;
        }

        .scheduler-chip:hover:not(:disabled) {
          border-color: var(--border-strong);
        }

        .scheduler-chip strong {
          color: var(--text);
          font-weight: 600;
        }

        .scheduler-chip-dot {
          width: 8px;
          height: 8px;
          border-radius: 50%;
          background: var(--success);
          box-shadow: 0 0 0 3px var(--success-soft);
        }

        .scheduler-chip.is-paused .scheduler-chip-dot {
          background: var(--attention);
          box-shadow: 0 0 0 3px var(--attention-soft);
        }

        .scheduler-chip-error {
          position: absolute;
          top: calc(100% + 6px);
          right: 0;
          padding: 6px 10px;
          border-radius: var(--radius-sm);
          background: var(--surface-3);
          border: 1px solid var(--danger);
          color: var(--danger-text);
          font-size: var(--fs-xs);
          white-space: nowrap;
          cursor: pointer;
        }

        .btn-logout {
          background-color: transparent;
          color: #a0a0b0;
          border: 1px solid #3a3a5c;
        }

        .btn-logout:hover {
          background-color: #2d2d44;
          color: #e0e0e0;
        }

        /* Phone: the tab bar and its More menu carry the navigation, the user
           and Log out, so the header keeps the brand and the scheduler. */
        @media (max-width: 767px) {
          .header {
            height: 3.5rem;
          }

          .header-nav,
          .user-name,
          .btn-logout,
          .scheduler-chip-label {
            display: none;
          }

          .scheduler-chip {
            height: 36px;
          }
        }
      `}</style>
    </header>
  );
}
