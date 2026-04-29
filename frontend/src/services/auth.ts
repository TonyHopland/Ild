import { api } from "./api";
import {
  User,
  WorkItem,
  LoopTemplate,
  LoopRun,
  Repository,
  RemoteProvider,
  AiProvider,
} from "../types";

interface BackendLoginResponse {
  token: string;
  username: string;
  expiresAt: string;
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
  },

  clearAuth: (): void => {
    localStorage.removeItem("auth_token");
    localStorage.removeItem("auth_user");
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

  getRuns: async (id: string): Promise<LoopRun[]> => {
    return api.get<LoopRun[]>(`/workitems/${id}/runs`);
  },

  linkPr: async (id: string, prUrl: string): Promise<void> => {
    return api.post<void>(`/workitems/${id}/link-pr`, { prUrl });
  },

  markMerged: async (id: string): Promise<void> => {
    return api.post<void>(`/workitems/${id}/mark-merged`, {});
  },
};

export const loopTemplateService = {
  getAll: async (): Promise<LoopTemplate[]> => {
    return api.get<LoopTemplate[]>("/looptemplates");
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
};

export const loopRunService = {
  getAll: async (): Promise<LoopRun[]> => {
    return api.get<LoopRun[]>("/looptemplates");
  },

  getById: async (id: string): Promise<LoopRun> => {
    return api.get<LoopRun>(`/looprun/${id}`);
  },

  trigger: async (workItemId: string): Promise<LoopRun> => {
    return api.post<LoopRun>(`/workitems/${workItemId}/start`, {});
  },

  cancel: async (id: string): Promise<void> => {
    return api.post<void>(`/looprun/${id}/cancel`, {});
  },
};

export const repositoryService = {
  getAll: async (): Promise<Repository[]> => {
    return api.get<Repository[]>("/repositories");
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
  getAll: async (): Promise<RemoteProvider[]> => {
    return api.get<RemoteProvider[]>("/remoteproviders");
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
  getAll: async (): Promise<AiProvider[]> => {
    return api.get<AiProvider[]>("/aiproviders");
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
