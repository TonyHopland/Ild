import type { AiProvider } from "../../../types";

/**
 * The tool keys an AI node starts with, given its saved `toolAllowlist`.
 * Mirrors the backend's `AiToolCatalog.NormalizeSelectedToolKeys`: unsupported
 * keys are dropped and an empty result means the provider defaults — except for
 * Copilot, whose only tool is "ild", where a saved list without it means ILD off.
 */
export function resolveToolSelection(
  provider: AiProvider | null,
  configuredTools: unknown,
): string[] {
  const supportedTools = provider?.supportedTools ?? [];
  if (supportedTools.length === 0) return [];

  const supportedToolKeys = new Set(supportedTools.map((tool) => tool.key));
  const explicitTools = Array.isArray(configuredTools)
    ? configuredTools.filter(
        (tool): tool is string => typeof tool === "string" && supportedToolKeys.has(tool),
      )
    : [];

  if (explicitTools.length > 0) return explicitTools;
  if (provider?.type === "copilot" && Array.isArray(configuredTools)) return explicitTools;

  return supportedTools.filter((tool) => tool.defaultEnabled).map((tool) => tool.key);
}
