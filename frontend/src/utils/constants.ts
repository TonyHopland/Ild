import type { IconName } from "../components/Icon";
import { WORK_ITEM_STATUS_ORDER, workItemStatusLabel } from "./labels";

export const APP_NAME = "ILD";

// The board's columns, left to right, named as utils/labels.ts names each status.
export const WORK_ITEM_STATUSES = WORK_ITEM_STATUS_ORDER.map((value) => ({
  value,
  label: workItemStatusLabel(value),
}));

// How many cards each Taskboard column fetches from the server per page.
export const TASKBOARD_PAGE_SIZE = 20;

export const WORK_ITEM_PRIORITIES = [
  { value: "Low", label: "Low", color: "#6b7280" },
  { value: "Medium", label: "Medium", color: "#f59e0b" },
  { value: "High", label: "High", color: "#ef4444" },
  { value: "Critical", label: "Critical", color: "#dc2626" },
] as const;

export const ROUTES = {
  HOME: "/",
  LOGIN: "/login",
  TASKBOARD: "/taskboard",
  LOOP_EDITOR: "/loop-editor",
  ANALYTICS: "/analytics",
  CHAT: "/chat",
  CHAT_SCHEDULES: "/chat/schedules",
  SETTINGS: "/settings",
  REPOSITORIES: "/repositories",
  AI_PROVIDERS: "/ai-providers",
  GLOBAL_VARIABLES: "/global-variables",
} as const;

/**
 * The app's pages, in menu order: the header's nav on a wide screen. On a
 * phone the ones with a `tab` label sit on the tab bar under it, and the rest
 * go in the More menu.
 */
export const NAV_ITEMS: readonly { label: string; path: string; icon: IconName; tab?: string }[] = [
  { label: "Taskboard", path: ROUTES.TASKBOARD, icon: "board", tab: "Taskboard" },
  { label: "Loop Editor", path: ROUTES.LOOP_EDITOR, icon: "loop", tab: "Loops" },
  { label: "Analytics", path: ROUTES.ANALYTICS, icon: "chart", tab: "Analytics" },
  { label: "Chat", path: ROUTES.CHAT, icon: "chat", tab: "Chat" },
  { label: "Settings", path: ROUTES.SETTINGS, icon: "gear" },
  { label: "Repositories", path: ROUTES.REPOSITORIES, icon: "repo" },
  { label: "AI Providers", path: ROUTES.AI_PROVIDERS, icon: "chat" },
  { label: "Global Variables", path: ROUTES.GLOBAL_VARIABLES, icon: "gear" },
];

export const DEFAULT_PAGE_SIZE = 20;
