export interface User {
  id: string;
  username: string;
  email: string;
  role: UserRole;
}

export enum UserRole {
  Admin = "admin",
  Member = "member",
  Viewer = "viewer",
}

export interface AuthState {
  user: User | null;
  token: string | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  login: (username: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
}

export enum WorkItemStatus {
  Backlog = "backlog",
  Ready = "ready",
  InProgress = "in_progress",
  InReview = "in_review",
  Done = "done",
}

export enum WorkItemPriority {
  Low = "low",
  Medium = "medium",
  High = "high",
  Critical = "critical",
}

export enum WorkItemType {
  Feature = "feature",
  Bug = "bug",
  Task = "task",
  Epic = "epic",
}

export interface WorkItem {
  id: string;
  title: string;
  description: string;
  status: WorkItemStatus;
  priority: WorkItemPriority;
  type: WorkItemType;
  assigneeId: string | null;
  assigneeName: string | null;
  creatorId: string;
  creatorName: string;
  createdAt: string;
  updatedAt: string;
  dueDate: string | null;
  tags: string[];
  parentId: string | null;
  order: number;
}

export interface LoopTemplate {
  id: string;
  name: string;
  description: string;
  intervalMinutes: number;
  steps: LoopStep[];
  isActive: boolean;
  createdBy: string;
  createdAt: string;
  updatedAt: string;
}

export interface LoopStep {
  id: string;
  order: number;
  type: LoopStepType;
  config: Record<string, unknown>;
  condition: string | null;
}

export enum LoopStepType {
  ApiCall = "api_call",
  Script = "script",
  Notification = "notification",
  Delay = "delay",
  Condition = "condition",
}

export interface LoopRun {
  id: string;
  templateId: string;
  templateName: string;
  status: LoopRunStatus;
  startedAt: string;
  completedAt: string | null;
  durationMs: number | null;
  steps: LoopRunStep[];
  error: string | null;
  triggeredBy: string;
}

export enum LoopRunStatus {
  Pending = "pending",
  Running = "running",
  Completed = "completed",
  Failed = "failed",
  Cancelled = "cancelled",
}

export interface LoopRunStep {
  id: string;
  stepId: string;
  stepName: string;
  status: LoopRunStepStatus;
  startedAt: string;
  completedAt: string | null;
  result: unknown;
  error: string | null;
}

export enum LoopRunStepStatus {
  Pending = "pending",
  Running = "running",
  Completed = "completed",
  Failed = "failed",
  Skipped = "skipped",
}

export interface SignalRMessage {
  type: SignalRMessageType;
  payload: unknown;
  timestamp: string;
}

export enum SignalRMessageType {
  WorkItemUpdated = "work_item_updated",
  WorkItemCreated = "work_item_created",
  WorkItemDeleted = "work_item_deleted",
  LoopRunStarted = "loop_run_started",
  LoopRunUpdated = "loop_run_updated",
  LoopRunCompleted = "loop_run_completed",
  Notification = "notification",
}

export interface ApiError {
  status: number;
  message: string;
  details?: unknown;
}

export interface PaginatedResponse<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}
