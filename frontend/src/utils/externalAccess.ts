import type { ExternalAgentGrantScope } from "../types";

export const mcpUrl = (baseUrl: string) => `${baseUrl}/mcp`;

export const agentApiUrl = (baseUrl: string) => `${baseUrl}/api/v1/agent`;

export const discoveryUrl = (baseUrl: string) => `${agentApiUrl(baseUrl)}/_about`;

/** The levels a grant can have, in the order the operator is offered them, with what each allows. */
export const GRANT_SCOPES: { value: ExternalAgentGrantScope; label: string; allows: string }[] = [
  {
    value: "ReadOnly",
    label: "Read only",
    allows: "Can look at work items, repositories and loops, but not create work items or propose.",
  },
  {
    value: "Full",
    label: "Read, create and propose",
    allows:
      "Can also create work items in the Backlog, edit its own, and propose edits to others. It cannot start work.",
  },
  {
    value: "Control",
    label: "Control of work (start, reorder and answer)",
    allows:
      "Can also start, queue and reorder any work item (within its status) and answer its human-feedback questions, so it can drive work to done without you. Starting work spends AI provider money. Merging and settings stay with you.",
  },
];

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
      : scope === "Control"
        ? "You can read, create work items, and make proposals. You can also start, queue or reorder any work item (reordering keeps its status; the lowest priority runs first) and answer its human-feedback stops (give input, respond, reject, pick an edge). Transitions to other statuses and merging stay with the operator."
        : "You can read, create work items in the Backlog, and make proposals. You cannot start work.",
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
