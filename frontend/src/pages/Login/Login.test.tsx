import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import Login from "./index";
import { AuthContext } from "../../hooks/useAuth";
import type { AuthState } from "../../types";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function renderLogin(login: AuthState["login"]) {
  const auth: AuthState = {
    user: null,
    token: null,
    isAuthenticated: false,
    isLoading: false,
    login,
    logout: vi.fn(),
  };
  return render(
    <AuthContext.Provider value={auth}>
      <MemoryRouter>
        <Login />
      </MemoryRouter>
    </AuthContext.Provider>,
  );
}

describe("Login page", () => {
  test("submits username and password to auth.login", async () => {
    const login = vi.fn().mockResolvedValue(undefined);
    renderLogin(login);

    fireEvent.change(screen.getByLabelText("Username"), { target: { value: "alice" } });
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "secret" } });
    fireEvent.click(screen.getByRole("button", { name: /sign in/i }));

    await waitFor(() => {
      expect(login).toHaveBeenCalledWith("alice", "secret");
    });
  });

  test("displays error message when login fails", async () => {
    const login = vi.fn().mockRejectedValue(new Error("Invalid credentials"));
    renderLogin(login);

    fireEvent.change(screen.getByLabelText("Username"), { target: { value: "alice" } });
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "wrong" } });
    fireEvent.click(screen.getByRole("button", { name: /sign in/i }));

    await waitFor(() => {
      expect(screen.queryByText("Invalid credentials")).not.toBeNull();
    });
  });
});
