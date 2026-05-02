import { NavLink } from "react-router-dom";
import { useAuth } from "../hooks/useAuth";
import { NAV_ITEMS, APP_NAME } from "../utils/constants";

export default function Header() {
  const { user, logout } = useAuth();

  return (
    <header className="header">
      <div className="header-content">
        <div className="header-brand">
          <NavLink to="/taskboard">{APP_NAME}</NavLink>
        </div>
        <nav className="header-nav">
          {NAV_ITEMS.map((item) => (
            <NavLink
              key={item.path}
              to={item.path}
              className={({ isActive }) => (isActive ? "nav-link active" : "nav-link")}
            >
              {item.label}
            </NavLink>
          ))}
        </nav>
        <div className="header-user">
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
          background-color: #1a1a2e;
          border-bottom: 1px solid #2d2d44;
          height: 4rem;
        }

        .header-content {
          display: flex;
          align-items: center;
          justify-content: space-between;
          width: 100%;
          padding: 0 1rem;
          height: 100%;
        }

        .header-brand a {
          font-size: 1.25rem;
          font-weight: 700;
          color: #e0e0e0;
          text-decoration: none;
        }

        .header-nav {
          display: flex;
          gap: 0.5rem;
        }

        .nav-link {
          padding: 0.5rem 1rem;
          border-radius: 0.375rem;
          color: #a0a0b0;
          text-decoration: none;
          font-size: 0.875rem;
          transition: all 0.15s ease;
        }

        .nav-link:hover {
          color: #e0e0e0;
          background-color: #2d2d44;
        }

        .nav-link.active {
          color: #fff;
          background-color: #3a3a5c;
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

        .btn {
          padding: 0.375rem 0.75rem;
          border-radius: 0.375rem;
          border: none;
          cursor: pointer;
          font-size: 0.875rem;
          transition: all 0.15s ease;
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
      `}</style>
    </header>
  );
}
