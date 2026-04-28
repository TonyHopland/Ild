export interface User {
  id: string;
  username: string;
  createdAt: string;
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
  Backlog = "Backlog",
  WorkQueue = "WorkQueue",
  Ready = "Ready",
  Running = "Running",
  HumanFeedback = "HumanFeedback",
  Done = "Done",
}

export enum WorkItemPriority {
  Low = "Low",
  Medium = "Medium",
  High = "High",
  Critical = "Critical",
}

export interface WorkItem {
  id: string;
  title: string;
  description: string;
  status: WorkItemStatus;
  priority: WorkItemPriority;
  labels: string[];
  loopTemplateId: string;
  loopTemplateVersion: string;
  repositoryId: string;
  pullRequestUrl: string | null;
  pullRequestBranch: string | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  dependencyIds: string[];
  dependentIds: string[];
}

export enum NodeType {
  Start = "Start",
  Cmd = "Cmd",
  AI = "AI",
  Human = "Human",
  Cleanup = "Cleanup",
}

export enum EdgeType {
  OnSuccess = "OnSuccess",
  OnFailure = "OnFailure",
}

export interface LoopNode {
  id: string;
  type: NodeType;
  label: string;
  config: Record<string, unknown>;
  maxTraversals: number | null;
  retryCount: number | null;
  timeoutSeconds: number | null;
}

export interface LoopNodeEdge {
  id: string;
  sourceNodeId: string;
  targetNodeId: string;
  edgeType: EdgeType;
  maxTraversals: number | null;
}

export interface LoopTemplate {
  id: string;
  name: string;
  description: string;
  version: number;
  nodes: LoopNode[];
  edges: LoopNodeEdge[];
  createdAt: string;
  updatedAt: string;
}

export enum LoopRunStatus {
  Running = "Running",
  Completed = "Completed",
  Failed = "Failed",
  Cancelled = "Cancelled",
}

export enum LoopRunNodeStatus {
  Pending = "Pending",
  Running = "Running",
  Succeeded = "Succeeded",
  Failed = "Failed",
  Skipped = "Skipped",
  WaitingHuman = "WaitingHuman",
}

export interface LoopRunNode {
  id: string;
  nodeId: string;
  nodeLabel: string;
  status: LoopRunNodeStatus;
  output: string | null;
  error: string | null;
  startedAt: string | null;
  completedAt: string | null;
  executionCount: number;
}

export interface LoopRun {
  id: string;
  workItemId: string;
  loopTemplateId: string;
  templateVersion: number;
  status: LoopRunStatus;
  currentNodeId: string | null;
  isPaused: boolean;
  nodeExecutionCount: number;
  startedAt: string;
  completedAt: string | null;
  nodes: LoopRunNode[];
}

export enum RecoveryPolicy {
  AutoResume = "AutoResume",
  NeedsReview = "NeedsReview",
  Cancel = "Cancel",
}

export interface EventLogEntry {
  sequence: number;
  runId: string;
  eventType: string;
  nodeId: string | null;
  payload: string;
  timestamp: string;
}

export interface Repository {
  id: string;
  name: string;
  remoteProviderId: string;
  cloneUrl: string;
  defaultBranch: string | null;
  worktreesPath: string | null;
  createdAt: string;
}

export interface RemoteProvider {
  id: string;
  name: string;
  type: string;
  baseUrl: string;
  apiKey: string;
  webhookSecret: string;
  createdAt: string;
}

export interface AiProvider {
  id: string;
  name: string;
  type: string;
  baseUrl: string;
  apiKey: string;
  model: string;
  isDefault: boolean;
  createdAt: string;
}

export interface ApiError {
  status: number;
  message: string;
  details?: unknown;
}

export interface SignalRMessage {
  type: string;
  payload: unknown;
  timestamp: string;
}
