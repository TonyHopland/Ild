import type { ConversationMessage, LoopRun } from "../../types";

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
 * has any: an older entry without that link shows none rather than a guess. A
 * write that left the value as it was is no change and does not count.
 */
export function variablesSetByTurn(message: ConversationMessage, runs: LoopRun[]): TurnVariable[] {
  const execution = message.runNodeId;
  if (!execution || message.role.toLowerCase() === "human") return [];
  const writes = runs.find((r) =>
    r.variableWrites?.some((w) => w.runNodeId === execution),
  )?.variableWrites;
  if (!writes) return [];

  const result: TurnVariable[] = [];
  const names = [...new Set(writes.filter((w) => w.runNodeId === execution).map((w) => w.name))];
  for (const name of names) {
    const history = writes.filter((w) => w.name === name);
    const here = history.flatMap((w, i) => (w.runNodeId === execution ? [i] : []));
    const firstHere = here[0];
    const lastHere = here[here.length - 1];
    const before = firstHere > 0 ? history[firstHere - 1].value : undefined;
    const value = history[lastHere].value;
    if (value === before) continue;
    result.push({
      name,
      value,
      change: before === undefined ? "created" : "changed",
      changedLater: history.slice(lastHere + 1).some((w) => w.value !== value),
    });
  }
  return result.sort((a, b) => a.name.localeCompare(b.name));
}
