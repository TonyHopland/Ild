export const APP_NAME = "ILD";

// The board's columns, left to right, named as utils/labels.ts names each status.
export const WORK_ITEM_STATUSES = [
  { value: "Backlog", label: "Backlog" },
  { value: "WorkQueue", label: "Work queue" },
  { value: "Ready", label: "Ready" },
  { value: "Running", label: "Running" },
  { value: "HumanFeedback", label: "Needs you" },
  { value: "WaitingForIld", label: "Waiting for ILD" },
  { value: "Done", label: "Done" },
] as const;

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

export const NAV_ITEMS = [
  { label: "Taskboard", path: ROUTES.TASKBOARD },
  { label: "Loop Editor", path: ROUTES.LOOP_EDITOR },
  { label: "Analytics", path: ROUTES.ANALYTICS },
  { label: "Chat", path: ROUTES.CHAT },
  { label: "Settings", path: ROUTES.SETTINGS },
  { label: "Repositories", path: ROUTES.REPOSITORIES },
  { label: "AI Providers", path: ROUTES.AI_PROVIDERS },
  { label: "Global Variables", path: ROUTES.GLOBAL_VARIABLES },
] as const;

export const DEFAULT_PAGE_SIZE = 20;
