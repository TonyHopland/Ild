import type { ConversationMessage, TurnVariableChange } from "../../types";

/**
 * The variables an AI turn created or changed, as the server reduced them from
 * the execution's writes. Only a turn that names the node execution it came
 * from has any: an older entry without that link shows none rather than a
 * guess.
 */
export function variablesSetByTurn(
  message: ConversationMessage,
  changes: TurnVariableChange[],
): TurnVariableChange[] {
  const execution = message.runNodeId;
  if (!execution || message.role.toLowerCase() === "human") return [];
  return changes
    .filter((c) => c.runNodeId === execution)
    .sort((a, b) => a.name.localeCompare(b.name));
}
