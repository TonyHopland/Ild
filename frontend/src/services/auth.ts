import { api } from "./api";
import { User, WorkItem, LoopTemplate, LoopRun } from "../types";

export const authService = {
  login: async (username: string, password: string): Promise<{ user: User; token: string }> => {
    return api.post<{ user: User; token: string }>("/auth/login", {
      username,
      password,
    });
  },

  logout: async (): Promise<void> => {
    await api.post("/auth/logout", {});
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
    return api.get<WorkItem[]>("/work-items");
  },

  getById: async (id: string): Promise<WorkItem> => {
    return api.get<WorkItem>(`/work-items/${id}`);
  },

  create: async (data: Partial<WorkItem>): Promise<WorkItem> => {
    return api.post<WorkItem>("/work-items", data);
  },

  update: async (id: string, data: Partial<WorkItem>): Promise<WorkItem> => {
    return api.put<WorkItem>(`/work-items/${id}`, data);
  },

  delete: async (id: string): Promise<void> => {
    return api.delete<void>(`/work-items/${id}`);
  },
};

export const loopTemplateService = {
  getAll: async (): Promise<LoopTemplate[]> => {
    return api.get<LoopTemplate[]>("/loop-templates");
  },

  getById: async (id: string): Promise<LoopTemplate> => {
    return api.get<LoopTemplate>(`/loop-templates/${id}`);
  },

  create: async (data: Partial<LoopTemplate>): Promise<LoopTemplate> => {
    return api.post<LoopTemplate>("/loop-templates", data);
  },

  update: async (id: string, data: Partial<LoopTemplate>): Promise<LoopTemplate> => {
    return api.put<LoopTemplate>(`/loop-templates/${id}`, data);
  },

  delete: async (id: string): Promise<void> => {
    return api.delete<void>(`/loop-templates/${id}`);
  },
};

export const loopRunService = {
  getAll: async (): Promise<LoopRun[]> => {
    return api.get<LoopRun[]>("/loop-runs");
  },

  getById: async (id: string): Promise<LoopRun> => {
    return api.get<LoopRun>(`/loop-runs/${id}`);
  },

  trigger: async (templateId: string): Promise<LoopRun> => {
    return api.post<LoopRun>(`/loop-runs/trigger/${templateId}`, {});
  },

  cancel: async (id: string): Promise<void> => {
    return api.post<void>(`/loop-runs/${id}/cancel`, {});
  },
};
