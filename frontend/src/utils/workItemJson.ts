import type { ConversationMessage, WorkItem } from "../types";

/**
 * Parse a server-side `TagsJson` payload (a stringified JSON array of
 * strings) into a tag[]. Tolerates null/empty/invalid JSON by returning
 * an empty array — the UI never crashes if the server sends garbage.
 */
export function parseTags(workItem: Pick<WorkItem, "tagsJson">): string[] {
  if (!workItem.tagsJson) return [];
  try {
    const parsed = JSON.parse(workItem.tagsJson);
    if (Array.isArray(parsed)) {
      return parsed.filter((t): t is string => typeof t === "string");
    }
  } catch {
    // fall through
  }
  return [];
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
