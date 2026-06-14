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
 * Build a predicate that reports whether a tag is a "loop tag" — i.e. it names
 * a loop template the engine could resolve and run. Matching is
 * case-insensitive, mirroring the backend resolver (DbLoopTemplateResolver),
 * which picks a template when a tag equals its name ignoring case. Tags that
 * match no template are ordinary, free-form labels.
 */
export function makeLoopTagMatcher(loopTemplateNames: readonly string[]): (tag: string) => boolean {
  const names = new Set(loopTemplateNames.map((n) => n.toLowerCase()));
  return (tag: string) => names.has(tag.toLowerCase());
}

/**
 * Sentinel content the backend writes when a Human node parks a run awaiting
 * free-form input (`HumanFeedbackReasons.HumanInputNeeded`). It carries no
 * dialogue — the prompt lives in the feedback banner — so it is suppressed
 * from the rendered conversation thread. The author varies (the node's label
 * can surface it under a "Human" bubble), so the sentinel is matched by its
 * exact content rather than by role.
 */
const HUMAN_INPUT_NEEDED = "Human Input Needed";

/**
 * Safely read the {@link ConversationMessage} array from a WorkItem.
 * Tolerates null/missing and filters out malformed entries, as well as the
 * "Human Input Needed" sentinel that precedes each human response.
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
      typeof m.timestamp === "string" &&
      m.content !== HUMAN_INPUT_NEEDED
    ) {
      out.push({
        role: m.role,
        content: m.content,
        timestamp: m.timestamp,
        name: typeof m.name === "string" ? m.name : null,
      });
    }
  }
  return out;
}
