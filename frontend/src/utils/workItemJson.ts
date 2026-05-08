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
 * Parse a server-side `ConversationJson` payload (a stringified JSON
 * array of {@link ConversationMessage}) into typed messages. Tolerates
 * malformed entries by skipping them.
 */
export function parseConversation(
  workItem: Pick<WorkItem, "conversationJson">,
): ConversationMessage[] {
  if (!workItem.conversationJson) return [];
  try {
    const parsed = JSON.parse(workItem.conversationJson);
    if (!Array.isArray(parsed)) return [];
    const out: ConversationMessage[] = [];
    for (const m of parsed) {
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
  } catch {
    return [];
  }
}
