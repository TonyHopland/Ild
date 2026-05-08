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
  WaitingForIld = "WaitingForIld",
  Done = "Done",
}

export enum WorkItemPriority {
  Low = "Low",
  Medium = "Medium",
  High = "High",
  Critical = "Critical",
}

export interface ConversationMessage {
  role: string;
  content: string;
  timestamp: string;
}

export interface WorkItem {
  id: string;
  title: string;
  description: string;
  status: WorkItemStatus;
  priority: WorkItemPriority;
  labels: string[];
  /**
   * JSON-encoded string[] of tags mirrored from the WorkItemServer.
   * Use {@link parseTags} to decode into a string[] for display.
   */
  tagsJson?: string | null;
  /**
   * JSON-encoded array of {@link ConversationMessage} mirrored from the
   * WorkItemServer. Use {@link parseConversation} to decode.
   */
  conversationJson?: string | null;
  loopTemplateId: string;
  loopTemplateVersion: string;
  repositoryId: string;
  prUrl: string | null;
  pullRequestBranch: string | null;
  humanFeedbackReason: string | null;
  humanFeedbackActions: string | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  currentLoopRunId: string | null;
  dependencyIds: string[];
  dependentIds: string[];
}

export enum NodeType {
  Start = "Start",
  Cmd = "Cmd",
  AI = "AI",
  Human = "Human",
  PR = "PR",
  Cleanup = "Cleanup",
}

export enum EdgeType {
  OnSuccess = "OnSuccess",
  OnFailure = "OnFailure",
  OnRespond = "OnRespond",
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
  isArchived: boolean;
}

export enum LoopRunStatus {
  Running = "Running",
  Completed = "Completed",
  Failed = "Failed",
  Cancelled = "Cancelled",
  WaitingHuman = "WaitingHuman",
}

export enum LoopRunNodeStatus {
  Pending = "Pending",
  Running = "Running",
  Succeeded = "Succeeded",
  Failed = "Failed",
  Skipped = "Skipped",
  WaitingHuman = "WaitingHuman",
  Responded = "Responded",
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

export interface EventLogEntry {
  sequence: number;
  runId: string;
  eventType: string;
  nodeId: string | null;
  payload: string;
  timestamp: string;
  hasPayload: boolean;
  nodeLabel?: string;
  runNodeId: string | null;
}

export interface EventLogPage {
  entries: EventLogEntry[];
  nextCursor: number;
  hasMore: boolean;
}

export interface Repository {
  id: string;
  name: string;
  remoteProviderId: string;
  cloneUrl: string;
  defaultBranch: string | null;
  worktreesPath: string | null;
  defaultIntakeStatus: WorkItemStatus;
  createdAt: string;
}

export interface RemoteProvider {
  id: string;
  name: string;
  type: string;
  baseUrl: string;
  apiKey: string;
  webhookSecret: string;
  workItemServerUrl?: string | null;
  workItemApiKey?: string | null;
  hasWorkItemApiKey?: boolean;
  pollIntervalSeconds?: number;
  graceIntervalSeconds?: number;
  maxConcurrentWorkItems?: number;
  createdAt: string;
}

export interface PrComment {
  id: string;
  body: string;
  author: string;
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

// SignalR payloads (mirror ILD.Data.DTOs.SignalRPayloads).
export interface NodeStateChangedPayload {
  runId: string;
  nodeId: string;
  oldStatus: LoopRunNodeStatus;
  newStatus: LoopRunNodeStatus;
}

export interface LoopRunStateChangedPayload {
  runId: string;
  oldStatus: LoopRunStatus;
  newStatus: LoopRunStatus;
}

export interface EventLoggedPayload {
  runId: string;
  message: string;
  eventType: string;
  nodeId: string | null;
  runNodeId: string | null;
}

export interface RunPausedPayload {
  runId: string;
}

export interface RunResumedPayload {
  runId: string;
}

export interface WorkItemStateChangedPayload {
  workItemId: string;
  oldStatus: WorkItemStatus;
  newStatus: WorkItemStatus;
}

export interface DependencyResolvedPayload {
  workItemId: string;
}

export interface HumanFeedbackRequiredPayload {
  workItemId: string;
  reason: string;
}

export interface NodeProgressPayload {
  runId: string;
  nodeId: string;
  line: string;
}

export interface LoopTemplateVersion {
  id: string;
  loopTemplateId: string;
  versionNumber: number;
  createdAt: string;
  nodeCount: number;
  edgeCount: number;
}

export enum ConfigFieldType {
  Text = "Text",
  Number = "Number",
  Toggle = "Toggle",
  Textarea = "Textarea",
  Select = "Select",
}

export interface ConfigFieldDescriptor {
  name: string;
  type: ConfigFieldType;
  label: string;
  required: boolean;
  defaultValue: string | number | boolean | null;
  description: string | null;
  options: string[] | null;
}
