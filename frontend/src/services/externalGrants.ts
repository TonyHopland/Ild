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

  revoke: (id: string) => api.post<void>(`/external-grants/${id}/revoke`, {}),

  revokeAll: () => api.post<{ revoked: number }>("/external-grants/revoke-all", {}),
};
