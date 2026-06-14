import type { AiMatchRule } from "../../types";

export type AdapterConfigValue = string | number | boolean;

/** Per-file outcome shown after a (possibly multi-file) template import. */
export interface ImportFeedbackItem {
  filename: string;
  status: "success" | "error" | "skipped";
  message: string;
}

export interface SessionPlaceholderUsage {
  name: string;
  count: number;
}

export interface NodeSettingsSnapshot {
  label: string;
  cmdCommand: string;
  aiPrompt: string;
  aiProvider: string;
  aiTools: string[];
  aiMatchRules: AiMatchRule[];
  customEdgeNames: string[];
  aiUseSession: boolean;
  aiSessionPlaceholder: string;
  startCreateWorktree: boolean;
  humanInputLabel: string;
  humanPrompt: string;
  promptNodePrompt: string;
  prDescriptionTemplate: string;
  prCommentTemplate: string;
  adapterConfigValues: Record<string, AdapterConfigValue>;
}

export interface LoopTemplateVersion {
  id: string;
  loopTemplateId: string;
  versionNumber: number;
  createdAt: string;
  nodeCount?: number | null;
}
