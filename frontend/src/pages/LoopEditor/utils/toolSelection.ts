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

  // The backend matches saved keys case-insensitively, so "ILD" must tick ILD here too.
  const canonicalKeys = new Map(supportedTools.map((tool) => [tool.key.toLowerCase(), tool.key]));
  const explicitTools = Array.isArray(configuredTools)
    ? [
        ...new Set(
          configuredTools.flatMap((tool) => {
            const key =
              typeof tool === "string" ? canonicalKeys.get(tool.toLowerCase()) : undefined;
            return key ? [key] : [];
          }),
        ),
      ]
    : [];

  if (explicitTools.length > 0) return explicitTools;
  const isCopilot = provider?.type?.trim().toLowerCase() === "copilot";
  if (isCopilot && Array.isArray(configuredTools)) return explicitTools;

  return supportedTools.filter((tool) => tool.defaultEnabled).map((tool) => tool.key);
}
