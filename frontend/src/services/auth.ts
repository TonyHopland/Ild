import { api } from "./api";
import {
  User,
  WorkItem,
  LoopTemplate,
  LoopRun,
  Repository,
  RemoteProvider,
  AiProvider,
  LoopTemplateVersion,
  EventLogPage,
  ConfigFieldDescriptor,
  LoopNode,
  LoopNodeEdge,
  PrComment,
} from "../types";

interface BackendLoginResponse {
  token: string;
  username: string;
  expiresAt: string;
}

function pageQuery(opts?: { skip?: number; take?: number }): string {
  if (!opts) return "";
  const params: string[] = [];
  if (opts.skip !== undefined) params.push(`skip=${opts.skip}`);
  if (opts.take !== undefined) params.push(`take=${opts.take}`);
  return params.length ? `?${params.join("&")}` : "";
}

const tokenListeners = new Set<(token: string | null) => void>();
function notifyTokenListeners(token: string | null) {
  tokenListeners.forEach((cb) => {
    try {
      cb(token);
    } catch {
      /* never throw out of a listener */
    }
  });
}

export const authService = {
  login: async (username: string, password: string): Promise<{ user: User; token: string }> => {
    const response = await api.post<BackendLoginResponse>("/auth/login", {
      username,
      password,
    });
    if (!response?.token) {
      throw { status: 401, message: "Invalid credentials" };
    }
    return {
      token: response.token,
      user: {
        id: response.username,
        username: response.username,
        createdAt: new Date().toISOString(),
      },
    };
  },

  logout: async (): Promise<void> => {
    try {
      await api.post("/auth/logout", {});
    } catch {
      // ignore — token may already be invalid
    }
    localStorage.removeItem("auth_token");
    localStorage.removeItem("auth_user");
    notifyTokenListeners(null);
  },

  getMe: async (): Promise<User> => {
    return api.get<User>("/auth/me");
  },

  getToken: (): string | null => {
    return localStorage.getItem("auth_token");
  },

  getUser: (): User | null => {
    const userStr = localStorage.getItem("auth_user");
    if (!userStr) return null;
    try {
      return JSON.parse(userStr) as User;
    } catch {
      return null;
    }
  },

  setAuth: (user: User, token: string): void => {
    localStorage.setItem("auth_token", token);
    localStorage.setItem("auth_user", JSON.stringify(user));
    notifyTokenListeners(token);
  },

  clearAuth: (): void => {
    localStorage.removeItem("auth_token");
    localStorage.removeItem("auth_user");
    notifyTokenListeners(null);
  },

  /**
   * Subscribe to auth token changes (login/logout). Returns an unsubscribe
   * function. Used by hooks that need to reconnect when the token changes.
   */
  onTokenChange: (cb: (token: string | null) => void): (() => void) => {
    tokenListeners.add(cb);
    return () => {
      tokenListeners.delete(cb);
    };
  },
};

export const workItemService = {
  getAll: async (): Promise<WorkItem[]> => {
    return api.get<WorkItem[]>("/workitems");
  },

  getById: async (id: string): Promise<WorkItem> => {
    return api.get<WorkItem>(`/workitems/${id}`);
  },

  create: async (data: Partial<WorkItem>): Promise<WorkItem> => {
    return api.post<WorkItem>("/workitems", data);
  },

  update: async (id: string, data: Partial<WorkItem>): Promise<WorkItem> => {
    return api.put<WorkItem>(`/workitems/${id}`, data);
  },

  delete: async (id: string): Promise<void> => {
    return api.delete<void>(`/workitems/${id}`);
  },

  getRuns: async (id: string, opts?: { skip?: number; take?: number }): Promise<LoopRun[]> => {
    return api.get<LoopRun[]>(`/workitems/${id}/runs${pageQuery(opts)}`);
  },

  linkPr: async (id: string, prUrl: string): Promise<void> => {
    return api.post<void>(`/workitems/${id}/link-pr`, { prUrl });
  },

  markMerged: async (id: string): Promise<void> => {
    return api.post<void>(`/workitems/${id}/mark-merged`, {});
  },

  humanFeedbackInput: async (id: string, input: string): Promise<void> => {
    return api.post<void>(`/workitems/${id}/human-feedback/input`, { input });
  },

  humanFeedbackReject: async (id: string, input?: string): Promise<void> => {
    return api.post<void>(`/workitems/${id}/human-feedback/reject`, input ? { input } : {});
  },

  getPrComments: async (id: string): Promise<PrComment[]> => {
    return api.get<PrComment[]>(`/workitems/${id}/pr-comments`);
  },

  cleanupToDone: async (id: string): Promise<void> => {
    return api.post<void>(`/workitems/${id}/cleanup-to-done`, {});
  },

  cleanupToBacklog: async (id: string): Promise<void> => {
    return api.post<void>(`/workitems/${id}/cleanup-to-backlog`, {});
  },

  transition: async (id: string, targetStatus: string): Promise<void> => {
    return api.post<void>(`/workitems/${id}/transition`, { targetStatus });
  },

  getDependencies: async (
    id: string,
    opts?: { skip?: number; take?: number },
  ): Promise<WorkItem[]> => {
    return api.get<WorkItem[]>(`/workitems/${id}/dependencies${pageQuery(opts)}`);
  },

  addDependency: async (id: string, dependencyId: string): Promise<void> => {
    return api.post<void>(`/workitems/${id}/dependencies`, { dependencyId });
  },

  removeDependency: async (id: string, dependencyId: string): Promise<void> => {
    return api.delete<void>(`/workitems/${id}/dependencies/${dependencyId}`);
  },
};

export const loopTemplateService = {
  getAll: async (opts?: { skip?: number; take?: number }): Promise<LoopTemplate[]> => {
    return api.get<LoopTemplate[]>(`/looptemplates${pageQuery(opts)}`);
  },

  getById: async (id: string): Promise<LoopTemplate> => {
    return api.get<LoopTemplate>(`/looptemplates/${id}`);
  },

  create: async (data: Partial<LoopTemplate>): Promise<LoopTemplate> => {
    return api.post<LoopTemplate>("/looptemplates", data);
  },

  update: async (id: string, data: Partial<LoopTemplate>): Promise<LoopTemplate> => {
    return api.put<LoopTemplate>(`/looptemplates/${id}`, data);
  },

  delete: async (id: string): Promise<void> => {
    return api.delete<void>(`/looptemplates/${id}`);
  },

  validate: async (data: unknown): Promise<{ valid: boolean; errors: string[] }> => {
    return api.post<{ valid: boolean; errors: string[] }>("/looptemplates/validate", data);
  },

  clone: async (id: string, newName: string): Promise<{ id: string }> => {
    return api.post<{ id: string }>(
      `/looptemplates/${id}/clone?newName=${encodeURIComponent(newName)}`,
      {},
    );
  },

  getVersions: async (id: string): Promise<LoopTemplateVersion[]> => {
    return api.get<LoopTemplateVersion[]>(`/looptemplates/${id}/versions`);
  },

  getVersionGraph: async (
    id: string,
    versionNumber: number,
  ): Promise<{ nodes: LoopNode[]; edges: LoopNodeEdge[] }> => {
    return api.get<{ nodes: LoopNode[]; edges: LoopNodeEdge[] }>(
      `/looptemplates/${id}/versions/${versionNumber}`,
    );
  },
};

export const loopRunService = {
  getAll: async (opts?: { skip?: number; take?: number }): Promise<LoopRun[]> => {
    return api.get<LoopRun[]>(`/loopruns${pageQuery(opts)}`);
  },

  getById: async (id: string): Promise<LoopRun> => {
    return api.get<LoopRun>(`/loopruns/${id}`);
  },

  trigger: async (workItemId: string): Promise<LoopRun> => {
    return api.post<LoopRun>(`/workitems/${workItemId}/start`, {});
  },

  cancel: async (id: string): Promise<void> => {
    return api.post<void>(`/loopruns/${id}/cancel`, {});
  },

  pause: async (id: string): Promise<void> => {
    return api.post<void>(`/loopruns/${id}/pause`, {});
  },

  resume: async (id: string): Promise<void> => {
    return api.post<void>(`/loopruns/${id}/resume`, {});
  },

  retryFromNode: async (id: string, runNodeId: string): Promise<void> => {
    return api.post<void>(`/loopruns/${id}/nodes/${runNodeId}/retry`, {});
  },

  getEvents: async (runId: string, cursor = 0, limit = 100): Promise<EventLogPage> => {
    return api.get<EventLogPage>(`/loopruns/${runId}/events?cursor=${cursor}&limit=${limit}`);
  },

  getPayload: async (runId: string, sequence: number): Promise<{ payload: string }> => {
    return api.get<{ payload: string }>(`/loopruns/${runId}/events/payload?sequence=${sequence}`);
  },
};

export const repositoryService = {
  getAll: async (opts?: { skip?: number; take?: number }): Promise<Repository[]> => {
    return api.get<Repository[]>(`/repositories${pageQuery(opts)}`);
  },

  getById: async (id: string): Promise<Repository> => {
    return api.get<Repository>(`/repositories/${id}`);
  },

  create: async (data: Partial<Repository>): Promise<Repository> => {
    return api.post<Repository>("/repositories", data);
  },

  update: async (id: string, data: Partial<Repository>): Promise<Repository> => {
    return api.put<Repository>(`/repositories/${id}`, data);
  },

  delete: async (id: string): Promise<void> => {
    return api.delete<void>(`/repositories/${id}`);
  },
};

export const remoteProviderService = {
  getAll: async (opts?: { skip?: number; take?: number }): Promise<RemoteProvider[]> => {
    return api.get<RemoteProvider[]>(`/remoteproviders${pageQuery(opts)}`);
  },

  getById: async (id: string): Promise<RemoteProvider> => {
    return api.get<RemoteProvider>(`/remoteproviders/${id}`);
  },

  create: async (data: Partial<RemoteProvider>): Promise<RemoteProvider> => {
    return api.post<RemoteProvider>("/remoteproviders", data);
  },

  update: async (id: string, data: Partial<RemoteProvider>): Promise<RemoteProvider> => {
    return api.put<RemoteProvider>(`/remoteproviders/${id}`, data);
  },

  delete: async (id: string): Promise<void> => {
    return api.delete<void>(`/remoteproviders/${id}`);
  },
};

export const aiProviderService = {
  getAll: async (opts?: { skip?: number; take?: number }): Promise<AiProvider[]> => {
    return api.get<AiProvider[]>(`/aiproviders${pageQuery(opts)}`);
  },

  getById: async (id: string): Promise<AiProvider> => {
    return api.get<AiProvider>(`/aiproviders/${id}`);
  },

  create: async (data: Partial<AiProvider>): Promise<AiProvider> => {
    return api.post<AiProvider>("/aiproviders", data);
  },

  update: async (id: string, data: Partial<AiProvider>): Promise<AiProvider> => {
    return api.put<AiProvider>(`/aiproviders/${id}`, data);
  },
};

export const loggingService = {
  setLevel: async (level: string): Promise<{ level: string }> => {
    return api.put<{ level: string }>("/logging/level", { level });
  },
};

export const agentAdapterService = {
  getSupportedProviderTypes: async (): Promise<string[]> => {
    return api.get<string[]>("/AgentAdapters");
  },
  getConfigSchema: async (providerType: string): Promise<ConfigFieldDescriptor[]> => {
    return api.get<ConfigFieldDescriptor[]>(`/AgentAdapters/${providerType}/config-schema`);
  },
};
