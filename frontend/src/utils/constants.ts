export const APP_NAME = "ILD";

export const WORK_ITEM_STATUSES = [
  { value: "Backlog", label: "Backlog" },
  { value: "WorkQueue", label: "Work Queue" },
  { value: "Ready", label: "Ready" },
  { value: "Running", label: "Running" },
  { value: "HumanFeedback", label: "Human Feedback" },
  { value: "WaitingForIld", label: "Waiting For Ild" },
  { value: "Done", label: "Done" },
] as const;

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
  LOOP_RUNS: "/loop-runs",
  SETTINGS: "/settings",
  REPOSITORIES: "/repositories",
  REMOTE_PROVIDERS: "/remote-providers",
  AI_PROVIDERS: "/ai-providers",
} as const;

export const NAV_ITEMS = [
  { label: "Taskboard", path: ROUTES.TASKBOARD },
  { label: "Loop Editor", path: ROUTES.LOOP_EDITOR },
  { label: "Loop Runs", path: ROUTES.LOOP_RUNS },
  { label: "Settings", path: ROUTES.SETTINGS },
  { label: "Repositories", path: ROUTES.REPOSITORIES },
  { label: "Remote Providers", path: ROUTES.REMOTE_PROVIDERS },
  { label: "AI Providers", path: ROUTES.AI_PROVIDERS },
] as const;

export const DEFAULT_PAGE_SIZE = 20;
