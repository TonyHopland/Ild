import { ApiError } from "../types";

const API_BASE: string = (import.meta.env?.VITE_API_BASE as string | undefined) ?? "/api/v1";

export const AUTH_UNAUTHORIZED_EVENT = "auth:unauthorized";

async function send(endpoint: string, options: RequestInit = {}): Promise<Response> {
  const token = localStorage.getItem("auth_token");

  // A FormData body must set its own Content-Type: the browser appends the
  // multipart boundary, which we could not know here.
  const isMultipart = options.body instanceof FormData;

  const headers: Record<string, string> = {
    ...(isMultipart ? {} : { "Content-Type": "application/json" }),
    ...(options.headers as Record<string, string>),
  };

  if (token) {
    headers["Authorization"] = `Bearer ${token}`;
  }

  const response = await fetch(`${API_BASE}${endpoint}`, {
    ...options,
    headers,
  });

  if (!response.ok) {
    if (response.status === 401 && !endpoint.startsWith("/auth/login")) {
      window.dispatchEvent(new CustomEvent(AUTH_UNAUTHORIZED_EVENT));
    }
    const errorBody = await response.text();
    let message = errorBody || response.statusText;
    try {
      const parsed = JSON.parse(errorBody);
      message = parsed.error || parsed.message || message;
    } catch {
      // not JSON, use raw body
    }
    const error: ApiError = {
      status: response.status,
      message,
    };
    throw error;
  }

  return response;
}

async function request<T>(endpoint: string, options: RequestInit = {}): Promise<T> {
  const response = await send(endpoint, options);

  const text = await response.text();
  if (!text) {
    return undefined as T;
  }

  return JSON.parse(text) as T;
}

export const api = {
  get: <T>(endpoint: string) => request<T>(endpoint, { method: "GET" }),

  /** A binary response (an attachment's bytes), for download or object-URL display. */
  getBlob: async (endpoint: string): Promise<Blob> =>
    (await send(endpoint, { method: "GET" })).blob(),

  /** A multipart upload. The caller assembles the FormData; auth and error handling are shared. */
  postForm: <T>(endpoint: string, form: FormData) =>
    request<T>(endpoint, { method: "POST", body: form }),
  post: <T>(endpoint: string, body: unknown) =>
    request<T>(endpoint, {
      method: "POST",
      body: JSON.stringify(body),
    }),
  put: <T>(endpoint: string, body: unknown) =>
    request<T>(endpoint, {
      method: "PUT",
      body: JSON.stringify(body),
    }),
  patch: <T>(endpoint: string, body: unknown) =>
    request<T>(endpoint, {
      method: "PATCH",
      body: JSON.stringify(body),
    }),
  delete: <T>(endpoint: string) => request<T>(endpoint, { method: "DELETE" }),
};
