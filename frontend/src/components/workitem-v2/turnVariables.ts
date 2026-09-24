import type { ConversationMessage, LoopRunVariableWrite } from "../../types";

export interface TurnVariable {
  name: string;
  /** The value as this turn left it, not the variable's current value. */
  value: string;
  change: "created" | "changed";
  changedLater: boolean;
}

/**
 * The variables an AI turn created or changed, each with the value it had at
 * the end of that turn. Only a turn that names the node execution it came from
 * has any: an older entry without that link shows none rather than a guess.
 * Whether a write created or changed a variable is what the server recorded at
 * the write, so it holds for variables set before history was kept. A turn
 * that left a variable as it found it did not change it.
 *
 * `writes` is the work item's history across its runs, oldest first.
 */
export function variablesSetByTurn(
  message: ConversationMessage,
  writes: LoopRunVariableWrite[],
): TurnVariable[] {
  const execution = message.runNodeId;
  if (!execution || message.role.toLowerCase() === "human") return [];

  const result: TurnVariable[] = [];
  const mine = writes.filter((w) => w.runNodeId === execution);
  for (const name of new Set(mine.map((w) => w.name))) {
    const here = mine.filter((w) => w.name === name);
    const first = here[0];
    const last = here[here.length - 1];
    if (first.previousValue !== null && last.value === first.previousValue) continue;
    const later = writes.slice(writes.indexOf(last) + 1);
    result.push({
      name,
      value: last.value,
      change: first.previousValue === null ? "created" : "changed",
      changedLater: later.some(
        (w) => w.runId === last.runId && w.name === name && w.value !== last.value,
      ),
    });
  }
  return result.sort((a, b) => a.name.localeCompare(b.name));
}
