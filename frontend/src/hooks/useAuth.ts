import { useState, useEffect, useCallback, createContext, useContext } from "react";
import { User, AuthState } from "../types";
import { authService } from "../services/auth";

const AuthContext = createContext<AuthState | null>(null);

export function useProvideAuth(): AuthState {
  const [user, setUser] = useState<User | null>(authService.getUser());
  const [token, setToken] = useState<string | null>(authService.getToken());
  const [isLoading, setIsLoading] = useState(true);

  useEffect(() => {
    const initAuth = async () => {
      const storedToken = authService.getToken();
      const storedUser = authService.getUser();

      if (storedToken && storedUser) {
        try {
          const me = await authService.getMe();
          setUser(me);
          setToken(storedToken);
        } catch {
          authService.clearAuth();
          setUser(null);
          setToken(null);
        }
      }
      setIsLoading(false);
    };

    initAuth();
  }, []);

  const login = useCallback(async (username: string, password: string) => {
    const result = await authService.login(username, password);
    authService.setAuth(result.user, result.token);
    setUser(result.user);
    setToken(result.token);
  }, []);

  const logout = useCallback(async () => {
    try {
      await authService.logout();
    } catch {
      authService.clearAuth();
    }
    setUser(null);
    setToken(null);
  }, []);

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
