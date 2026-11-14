import type { ExternalAgentGrantScope } from "../types";

export const mcpUrl = (baseUrl: string) => `${baseUrl}/mcp`;

export const agentApiUrl = (baseUrl: string) => `${baseUrl}/api/v1/agent`;

export const discoveryUrl = (baseUrl: string) => `${agentApiUrl(baseUrl)}/_about`;

/** What an AI session needs, in the words it can act on, to use the access it was handed. */
export function buildAccessInstructions(access: {
  baseUrl: string;
  token: string;
  expiresAt: string;
  scope: ExternalAgentGrantScope;
}): string {
  const { baseUrl, token, expiresAt, scope } = access;
  return [
    `You have temporary access to ILD, a coding-agent orchestrator, until ${new Date(expiresAt).toISOString()}.`,
    `Base URL: ${baseUrl}`,
    `Token: ${token}`,
    `REST: ${agentApiUrl(baseUrl)}/... with the header "Authorization: Bearer ${token}".`,
    `MCP (Streamable HTTP): ${mcpUrl(baseUrl)} with the same Authorization header.`,
    `Docs: GET ${discoveryUrl(baseUrl)} describes the API and needs no token.`,
    "You are your own session: you may edit or delete only the work items you created. Propose edits to anything else; a human approves or rejects each proposal.",
    scope === "ReadOnly"
      ? "This access is read-only: you can read but not create work items or make proposals."
      : "You can read, create work items in the Backlog, and make proposals.",
    "Never put the token in a URL.",
  ].join("\n");
}

/** Time left until `expiresAt` as of `now`, e.g. "2 h 5 min left", or "expired". */
export function formatTimeLeft(expiresAt: string, now: number): string {
  const left = new Date(expiresAt).getTime() - now;
  if (left <= 0) return "expired";
  const minutes = Math.ceil(left / 60_000);
  const days = Math.floor(minutes / 1440);
  const hours = Math.floor((minutes % 1440) / 60);
  const rest = minutes % 60;
  if (days > 0) return `${days} d ${hours} h left`;
  if (hours > 0) return `${hours} h ${rest} min left`;
  return `${minutes} min left`;
}
