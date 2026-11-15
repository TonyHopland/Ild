import { api } from "./api";
import type {
  ExternalAgentGrant,
  ExternalAgentGrantDetail,
  ExternalAgentGrantScope,
  IssuedExternalAgentGrant,
} from "../types";

export const externalGrantService = {
  list: () => api.get<ExternalAgentGrant[]>("/external-grants"),

  get: (id: string) => api.get<ExternalAgentGrantDetail>(`/external-grants/${id}`),

  create: (name: string, lifetimeMinutes: number, scope: ExternalAgentGrantScope) =>
    api.post<IssuedExternalAgentGrant>("/external-grants", { name, lifetimeMinutes, scope }),

  renew: (
    id: string,
    body: { lifetimeMinutes: number; scope: ExternalAgentGrantScope; name?: string },
  ) => api.post<IssuedExternalAgentGrant>(`/external-grants/${id}/renew`, body),

  revoke: (id: string) => api.post<void>(`/external-grants/${id}/revoke`, {}),

  revokeAll: () => api.post<{ revoked: number }>("/external-grants/revoke-all", {}),
};
