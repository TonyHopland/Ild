import type { RunConversationMessage, TurnVariableChange } from "../../types";

/**
 * The variables an AI turn created or changed, as the server reduced them from
 * the execution's writes. Only an AI turn has any, matched by the node
 * execution it came from.
 */
export function variablesSetByTurn(
  message: RunConversationMessage,
  changes: TurnVariableChange[],
): TurnVariableChange[] {
  const execution = message.runNodeId;
  if (!execution || message.role !== "ai") return [];
  return changes
    .filter((c) => c.runNodeId === execution)
    .sort((a, b) => a.name.localeCompare(b.name));
}
