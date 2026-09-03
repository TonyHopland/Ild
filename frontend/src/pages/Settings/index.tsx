import { NavLink, useParams } from "react-router";
import IldSettings from "./sections/IldSettings";
import UserSettings from "./sections/UserSettings";
import NetworkSettings from "./sections/NetworkSettings";
import LoggingSettings from "./sections/LoggingSettings";
import "./Settings.css";

function Icon({ path }: { path: string }) {
  return (
    <svg
      width="16"
      height="16"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d={path} />
    </svg>
  );
}

const SECTIONS = [
  {
    id: "ild",
    label: "Ild",
    icon: "M12 3v3m0 12v3m9-9h-3M6 12H3m14.5-6.5-2 2m-7 7-2 2m0-11 2 2m7 7 2 2M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z",
    Component: IldSettings,
  },
  {
    id: "user",
    label: "User",
    icon: "M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8Zm8 8a8 8 0 0 0-16 0",
    Component: UserSettings,
  },
  {
    id: "network",
    label: "Network",
    icon: "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18Zm0 0c2.5-2.5 3.5-5.5 3.5-9S14.5 5.5 12 3m0 18c-2.5-2.5-3.5-5.5-3.5-9S9.5 5.5 12 3M3.5 9h17m-17 6h17",
    Component: NetworkSettings,
  },
  {
    id: "logging",
    label: "Logging",
    icon: "M5 3h9l5 5v13H5V3Zm9 0v5h5M8 13h8m-8 4h5",
    Component: LoggingSettings,
  },
] as const;

/**
 * Settings, one page per group. The section comes from the URL so a page is
 * linkable and the browser's Back button walks the groups; `/settings` on its
 * own is the first one rather than a redirect, so the plain link still works.
 */
export default function Settings() {
  const { section } = useParams<{ section?: string }>();
  const active = SECTIONS.find((s) => s.id === section) ?? SECTIONS[0];
  const Section = active.Component;

  return (
    <div className="page-container">
      <h1 className="page-title">Settings</h1>
      <div className="settings-page">
        <nav className="settings-nav" aria-label="Settings sections">
          <span className="settings-nav-heading">Settings</span>
          {SECTIONS.map((s) => (
            <NavLink
              key={s.id}
              to={`/settings/${s.id}`}
              className={() =>
                s.id === active.id ? "settings-nav-link active" : "settings-nav-link"
              }
            >
              <Icon path={s.icon} />
              {s.label}
            </NavLink>
          ))}
        </nav>
        <div className="settings-pane">
          <Section />
        </div>
      </div>
    </div>
  );
}
