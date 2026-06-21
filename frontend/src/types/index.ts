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
  /** Author display name (e.g. the node's title). Falls back to role when absent. */
  name?: string | null;
}

export interface WorkItem {
  id: string;
  title: string;
  description: string;
  status: WorkItemStatus;
  priority: WorkItemPriority;
  tags: string[];
  /**
   * Array of {@link ConversationMessage} mirrored from the WorkItemServer.
   * Use {@link parseConversation} to safely read (handles null/missing).
   */
  conversation?: ConversationMessage[] | null;
  /**
   * Deprecated: template is resolved from {@link tags} at run start
   * (PRD §3.7). The server may still return it for legacy reasons.
   */
  loopTemplateId?: string;
  loopTemplateVersion?: string;
  repositoryId: string;
  prUrl: string | null;
  pullRequestBranch: string | null;
  humanFeedbackReason: string | null;
  humanFeedbackActions: string | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  currentLoopRunId: string | null;
  /** Label of the node the active run is currently executing; null when idle. */
  currentNodeLabel?: string | null;
  worktreePath?: string | null;
  branchName?: string | null;
  dependencyIds: string[];
  dependentIds: string[];
  isPreviewRunning?: boolean;
  /**
   * Badge-relevant PR status from the current run's heartbeat snapshot, surfaced
   * so the taskboard card can show the PR's CI/review/merge tags while the item
   * is parked awaiting human feedback. Null/undefined when there is no snapshot.
   */
  prStatus?: WorkItemPrStatus | null;
}

/**
 * Compact PR status carried on a {@link WorkItem} (mirrors the backend's
 * WorkItemPrStatus). A badge-relevant subset of {@link RemotePrSnapshot}.
 */
export interface WorkItemPrStatus {
  state: string;
  merged: boolean;
  mergeable: boolean | null;
  mergeableState: string | null;
  ci: RemotePrCiStatus;
  approved: boolean;
  changesRequested: boolean;
}

export interface WorktreePreviewService {
  name: string;
  portAlias: string;
  status: string;
  port: number | null;
  suggestedPort: number | null;
  healthUrl: string | null;
  publicUrl: string | null;
  logFilePath: string | null;
  processId: number | null;
  exitCode: number | null;
}

/** Tail of a preview service's captured stdout/stderr log. */
export interface WorktreePreviewLog {
  service: string;
  content: string | null;
}

export interface WorktreePreview {
  configured: boolean;
  state: string;
  worktreePath: string;
  configPath: string | null;
  profileName: string | null;
  publicHost: string | null;
  stateDirectory: string | null;

  message: string | null;
  services: WorktreePreviewService[];
}

/** A file's change status relative to the default branch's fork point. */
export type WorktreeFileChangeStatus = "none" | "added" | "modified" | "deleted";

export interface WorktreeFileEntry {
  path: string;
  changeStatus: WorktreeFileChangeStatus;
}

export interface WorktreeFiles {
  worktreePath: string;
  files: WorktreeFileEntry[];
}

export interface WorktreeFileContent {
  path: string;
  changeStatus: WorktreeFileChangeStatus;
  content: string | null;
  diff: string | null;
  isBinary: boolean;
}

export enum NodeType {
  Start = "Start",
  Cmd = "Cmd",
  AI = "AI",
  Human = "Human",
  Prompt = "Prompt",
  PR = "PR",
  Condition = "Condition",
  Cleanup = "Cleanup",
}

export enum EdgeType {
  OnSuccess = "OnSuccess",
  OnFailure = "OnFailure",
  // A named custom outlet. Only Human, AI and PR nodes may declare these, and a
  // node may declare any number of them as long as their names are unique. The
  // pair (edgeType, name) identifies the edge. Replaces the former OnRespond.
  Custom = "Custom",
}

export interface LoopNode {
  id: string;
  type: NodeType;
  label: string;
  config: Record<string, unknown>;
  maxTraversals: number | null;
}

export interface LoopNodeEdge {
  id: string;
  sourceNodeId: string;
  targetNodeId: string;
  edgeType: EdgeType;
  // Custom-edge key; null for default (OnSuccess) and fallback (OnFailure) edges.
  name?: string | null;
  maxTraversals: number | null;
}

export enum RecoveryPolicy {
  AutoResume = "AutoResume",
  NeedsReview = "NeedsReview",
  Cancel = "Cancel",
}

export interface LoopTemplate {
  id: string;
  name: string;
  description: string;
  version: number;
  recoveryPolicy: RecoveryPolicy;
  nodes: LoopNode[];
  edges: LoopNodeEdge[];
  createdAt: string;
  updatedAt: string;
  isArchived: boolean;
}

// Export file format (ild-loop-template/v1)
// LoopNodeExportNode is LoopNode minus maxTraversals (not part of the export schema)
export type LoopTemplateExportNode = Omit<LoopNode, "maxTraversals">;

export type LoopTemplateExportEdge = Omit<LoopNodeEdge, "maxTraversals">;

export interface LoopTemplateExport {
  $schema: "ild-loop-template/v1";
  name: string;
  description: string;
  recoveryPolicy: RecoveryPolicy;
  nodes: LoopTemplateExportNode[];
  edges: LoopTemplateExportEdge[];
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
  Interrupted = "Interrupted",
}

export interface LoopRunNode {
  id: string;
  nodeId: string;
  nodeLabel: string;
  status: LoopRunNodeStatus;
  effectiveInput: string | null;
  output: string | null;
  error: string | null;
  startedAt: string | null;
  completedAt: string | null;
  executionCount: number;
  /** Edge the engine traversed to reach this node; null for the run's first node. */
  incomingEdgeId?: string | null;
  /** Template node type (e.g. "AI"); only populated by the run-detail endpoint. */
  nodeType?: string | null;
  /** AI token/cost; null for non-AI nodes or turns with no reported usage. */
  inputTokens?: number | null;
  outputTokens?: number | null;
  costUsd?: number | null;
}

export interface LoopRunAvailableSession {
  adapterName: string;
  sessionId: string;
  createdAt: string;
  updatedAt: string | null;
  isCurrent: boolean;
  placeholders: string[];
}

export interface LoopRunSessionPreview {
  adapterName: string;
  sessionId: string;
  createdAt: string;
  updatedAt: string | null;
  sessionJson: string;
}

export interface LoopRunVariable {
  name: string;
  value: string;
  createdAt: string;
  updatedAt: string | null;
}

export interface LoopRun {
  id: string;
  workItemId: string;
  loopTemplateId: string;
  templateVersion: number;
  status: LoopRunStatus;
  currentNodeId: string | null;
  isPaused: boolean;
  isHalted?: boolean;
  retain?: boolean;
  worktreePath?: string | null;
  branchName?: string | null;
  nodeExecutionCount: number;
  startedAt: string;
  completedAt: string | null;
  /** Run-level token/cost totals summed from the run's nodes. */
  totalInputTokens?: number;
  totalOutputTokens?: number;
  totalCostUsd?: number | null;
  availableSessions?: LoopRunAvailableSession[];
  availableVariables?: LoopRunVariable[];
  nodes: LoopRunNode[];
  /** The run's linked PR URL, if a PR node has opened one. */
  prUrl?: string | null;
  /** Full PR snapshot from the heartbeat poller; null until the first poll. */
  prSnapshot?: RemotePrSnapshot | null;
}

/** Aggregate CI verdict for a PR's head commit (mirrors RemotePrCiStatus). */
export type RemotePrCiStatus = "None" | "Pending" | "Passed" | "Failed";

/** One entry in a PR's conversation (issue comment, review comment, or review). */
export interface RemotePrConversationEntry {
  kind: "comment" | "review_comment" | "review";
  author: string;
  body: string;
  createdAt: string;
  /** Review verdict (APPROVED / CHANGES_REQUESTED / …) when kind === "review". */
  state: string | null;
}

/** Full point-in-time PR view, persisted per run and rendered by the feedback UI. */
export interface RemotePrSnapshot {
  title: string | null;
  body: string | null;
  state: string;
  merged: boolean;
  mergeable: boolean | null;
  mergeableState: string | null;
  ci: RemotePrCiStatus;
  approved: boolean;
  changesRequested: boolean;
  conversation: RemotePrConversationEntry[];
  fetchedAt: string;
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
  hasApiKey?: boolean;
  webhookSecret: string;
  createdAt: string;
}

export interface WorkItemServerConfig {
  url?: string | null;
  apiKey?: string | null;
  hasApiKey?: boolean;
  pollIntervalSeconds: number;
  graceIntervalSeconds: number;
}

export interface RemoteProviderTypeOption {
  type: string;
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
  parallelism: number;
  supportedTools?: AiToolDefinition[];
  createdAt: string;
}

export interface AiToolDefinition {
  key: string;
  label: string;
  description: string;
  defaultEnabled: boolean;
}

/**
 * One AI output-matching rule: if {@link pattern} (a case-insensitive regex)
 * matches the AI output, the node routes to the custom edge named
 * {@link edgeName}. Rules are evaluated in order; the first match wins.
 * Stored on an AI node's config as `matchRules`.
 */
export interface AiMatchRule {
  pattern: string;
  edgeName: string;
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

export interface RunHaltedPayload {
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
  /** Monotonic per-run sequence number; used to dedupe the backlog→live handoff. */
  seq: number;
}

export interface PrSnapshotChangedPayload {
  runId: string;
}

export interface PreviewStateChangedPayload {
  workItemId: string;
}

export interface WorkItemRunProgressedPayload {
  workItemId: string;
}

export interface SchedulerStateChangedPayload {
  isPaused: boolean;
  maxConcurrent: number;
}

// ---- Chat (ADR-0010) ----

/** A renderable transcript turn. Mirrors ILD.Data.DTOs.ChatMessageView. */
export interface ChatMessage {
  id: string;
  role: "user" | "assistant";
  content: string;
  interrupted: boolean;
  sequence: number;
  createdAt: string;
}

/** The current user's chat session. Mirrors ILD.Data.DTOs.ChatSessionView. */
export interface ChatSession {
  id: string;
  aiProviderId: string;
  providerType: string;
  tools: string[];
  createdAt: string;
  messages: ChatMessage[];
}

export interface ChatMessageAppendedPayload {
  chatSessionId: string;
  message: ChatMessage;
}

export interface ChatTurnProgressPayload {
  chatSessionId: string;
  delta: string;
}

export interface ChatTurnCompletedPayload {
  chatSessionId: string;
  interrupted: boolean;
}

export interface AppSetting {
  key: string;
  value: string;
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

/** Per-loop-template analytics rollup (mirrors ILD.Data.DTOs.TemplateAnalytics). */
export interface TemplateAnalytics {
  templateId: string;
  templateName: string;
  totalRuns: number;
  completedRuns: number;
  failedRuns: number;
  cancelledRuns: number;
  /** Completed runs as a fraction (0..1) of terminal runs. */
  successRate: number;
  avgNodeSeconds: number | null;
  onFailureRoutings: number;
  rejectRoutings: number;
  avgHumanFeedbackSeconds: number | null;
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCostUsd: number;
}

/** Per agent-provider rollup (mirrors ILD.Data.DTOs.ProviderAnalytics). */
export interface ProviderAnalytics {
  provider: string;
  totalRuns: number;
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCostUsd: number;
}

/** One point in the cost/token/run time series (PeriodStart is a yyyy-MM-dd date). */
export interface AnalyticsSeriesPoint {
  periodStart: string;
  runs: number;
  inputTokens: number;
  outputTokens: number;
  costUsd: number;
}

/** Time-series bucket size. Matches the backend AnalyticsGranularity enum (PascalCase). */
export type AnalyticsGranularity = "Day" | "Week" | "Month" | "Year";

/** Dashboard filter sent to the analytics endpoint. */
export interface AnalyticsFilters {
  from?: string | null;
  to?: string | null;
  provider?: string | null;
  granularity?: AnalyticsGranularity;
}

/** Analytics dashboard payload (mirrors ILD.Data.DTOs.RunAnalyticsOverview). */
export interface RunAnalyticsOverview {
  totalRuns: number;
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCostUsd: number;
  templates: TemplateAnalytics[];
  providers: ProviderAnalytics[];
  series: AnalyticsSeriesPoint[];
  availableProviders: string[];
  granularity: AnalyticsGranularity;
}
