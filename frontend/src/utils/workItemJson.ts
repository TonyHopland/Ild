import type { ConversationMessage, WorkItem } from "../types";

/**
 * Get the tag list from a WorkItem. The API returns tags as a plain
 * string array, so this is a simple safe accessor that tolerates
 * null/missing data.
 */
export function parseTags(workItem: Pick<WorkItem, "tags">): string[] {
  return workItem.tags?.filter((t): t is string => typeof t === "string") ?? [];
}

/**
 * Safely read the {@link ConversationMessage} array from a WorkItem.
 * Tolerates null/missing and filters out malformed entries.
 */
export function parseConversation(workItem: Pick<WorkItem, "conversation">): ConversationMessage[] {
  if (!workItem.conversation || !Array.isArray(workItem.conversation)) return [];
  const out: ConversationMessage[] = [];
  for (const m of workItem.conversation) {
    if (
      m &&
      typeof m === "object" &&
      typeof m.role === "string" &&
      typeof m.content === "string" &&
      typeof m.timestamp === "string"
    ) {
      out.push({ role: m.role, content: m.content, timestamp: m.timestamp });
    }
  }
  return out;
}
