export const APP_NAME = "ILD";

export const API_BASE_URL = "/api";

export const WORK_ITEM_STATUSES = [
  { value: "backlog", label: "Backlog" },
  { value: "ready", label: "Ready" },
  { value: "in_progress", label: "In Progress" },
  { value: "in_review", label: "In Review" },
  { value: "done", label: "Done" },
] as const;

export const WORK_ITEM_PRIORITIES = [
  { value: "low", label: "Low", color: "#6b7280" },
  { value: "medium", label: "Medium", color: "#f59e0b" },
  { value: "high", label: "High", color: "#ef4444" },
  { value: "critical", label: "Critical", color: "#dc2626" },
] as const;

export const WORK_ITEM_TYPES = [
  { value: "feature", label: "Feature" },
  { value: "bug", label: "Bug" },
  { value: "task", label: "Task" },
  { value: "epic", label: "Epic" },
] as const;

export const ROUTES = {
  HOME: "/",
  LOGIN: "/login",
  TASKBOARD: "/taskboard",
  LOOP_EDITOR: "/loop-editor",
  LOOP_RUNS: "/loop-runs",
  SETTINGS: "/settings",
} as const;

export const NAV_ITEMS = [
  { label: "Taskboard", path: ROUTES.TASKBOARD },
  { label: "Loop Editor", path: ROUTES.LOOP_EDITOR },
  { label: "Loop Runs", path: ROUTES.LOOP_RUNS },
  { label: "Settings", path: ROUTES.SETTINGS },
] as const;

export const DEFAULT_PAGE_SIZE = 20;

export const SIGNALR_HUB_URL = "/api/signalr-hub";
