import { useState, useEffect, useCallback, createContext, useContext } from "react";
import { User, AuthState } from "../types";
import { authService } from "../services/auth";
import { AUTH_UNAUTHORIZED_EVENT } from "../services/api";

const AuthContext = createContext<AuthState | null>(null);

export function useProvideAuth(): AuthState {
  const [user, setUser] = useState<User | null>(authService.getUser());
  const [token, setToken] = useState<string | null>(authService.getToken());
  const [isLoading, setIsLoading] = useState(true);

  const clear = useCallback(() => {
    authService.clearAuth();
    setUser(null);
    setToken(null);
  }, []);

  // Validate any persisted token against the backend on mount.
  useEffect(() => {
    let cancelled = false;
    const init = async () => {
      const storedToken = authService.getToken();
      if (!storedToken) {
        setIsLoading(false);
        return;
      }
      try {
        const me = await authService.getMe();
        if (!cancelled) {
          setUser(me);
          setToken(storedToken);
        }
      } catch {
        if (!cancelled) clear();
      } finally {
        if (!cancelled) setIsLoading(false);
      }
    };
    void init();
    return () => {
      cancelled = true;
    };
  }, [clear]);

  // Any 401 from the API client tears down the session; the router will
  // render <Login> because isAuthenticated flips to false.
  useEffect(() => {
    const onUnauthorized = () => clear();
    window.addEventListener(AUTH_UNAUTHORIZED_EVENT, onUnauthorized);
    return () => window.removeEventListener(AUTH_UNAUTHORIZED_EVENT, onUnauthorized);
  }, [clear]);

  const login = useCallback(async (username: string, password: string) => {
    const result = await authService.login(username, password);
    authService.setAuth(result.user, result.token);
    setUser(result.user);
    setToken(result.token);
  }, []);

  const logout = useCallback(async () => {
    try {
      await authService.logout();
    } finally {
      clear();
    }
  }, [clear]);

  return {
    user,
    token,
    isAuthenticated: !!user && !!token,
    isLoading,
    login,
    logout,
  };
}

export function useAuth(): AuthState {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error("useAuth must be used within an AuthProvider");
  }
  return context;
}

export { AuthContext };
