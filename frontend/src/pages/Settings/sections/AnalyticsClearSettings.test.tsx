import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import AnalyticsClearSettings from "./AnalyticsClearSettings";
import * as services from "../../../services/auth";
import { deferred } from "../../../test-support";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const clearAll = () => screen.getByRole("button", { name: "Clear all analytics" });
const clearOlder = () => screen.getByRole("button", { name: /^Clear older than/ });
const days = () => screen.getByLabelText("Days") as HTMLInputElement;

describe("AnalyticsClearSettings", () => {
  test("says that runs ILD still keeps stay in analytics", () => {
    render(<AnalyticsClearSettings />);
    expect(screen.getByRole("heading", { name: "Analytics" })).toBeTruthy();
    expect(document.querySelector(".settings-card-note")?.textContent).toMatch(/runs/i);
  });

  test("clicking Clear all asks first, and Cancel sends nothing and returns focus", () => {
    const clear = vi.spyOn(services.analyticsService, "clear").mockResolvedValue();
    render(<AnalyticsClearSettings />);

    fireEvent.click(clearAll());
    const group = screen.getByRole("group");
    expect(group.textContent).toMatch(/cannot be undone/i);
    expect(clear).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(
      within(group).getByRole("button", { name: /confirm|clear/i }),
    );

    fireEvent.click(within(group).getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("group")).toBeNull();
    expect(clear).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(clearAll());
  });

  test("confirming Clear all sends no day count and shows a done message", async () => {
    const clear = vi.spyOn(services.analyticsService, "clear").mockResolvedValue();
    render(<AnalyticsClearSettings />);

    fireEvent.click(clearAll());
    fireEvent.click(
      within(screen.getByRole("group")).getByRole("button", { name: /confirm|clear/i }),
    );

    await waitFor(() => expect(screen.getByRole("status")).toBeTruthy());
    expect(clear).toHaveBeenCalledTimes(1);
    expect(clear.mock.calls[0][0]).toBeUndefined();
    expect(screen.queryByRole("group")).toBeNull();
  });

  test("the day count is fixed when the step is opened, and names the days", async () => {
    const clear = vi.spyOn(services.analyticsService, "clear").mockResolvedValue();
    render(<AnalyticsClearSettings />);

    fireEvent.change(days(), { target: { value: "30" } });
    fireEvent.click(clearOlder());
    const group = screen.getByRole("group");
    expect(group.textContent).toContain("30");
    expect(group.textContent).toMatch(/cannot be undone/i);
    fireEvent.change(days(), { target: { value: "99" } });
    fireEvent.click(within(group).getByRole("button", { name: /confirm|clear/i }));

    await waitFor(() => expect(clear).toHaveBeenCalledWith(30));
  });

  test("the older-than action cannot be started without a whole number of at least 1", () => {
    render(<AnalyticsClearSettings />);
    for (const bad of ["", "0", "-2", "1.5"]) {
      fireEvent.change(days(), { target: { value: bad } });
      expect((clearOlder() as HTMLButtonElement).disabled).toBe(true);
    }
    fireEvent.change(days(), { target: { value: "7" } });
    expect((clearOlder() as HTMLButtonElement).disabled).toBe(false);
  });

  test("while the request is in flight confirm, Cancel and both triggers are disabled", async () => {
    const pending = deferred<void>();
    vi.spyOn(services.analyticsService, "clear").mockReturnValue(pending.promise);
    render(<AnalyticsClearSettings />);
    fireEvent.change(days(), { target: { value: "7" } });

    fireEvent.click(clearAll());
    const group = screen.getByRole("group");
    fireEvent.click(within(group).getByRole("button", { name: /confirm|clear/i }));

    for (const button of within(screen.getByRole("group")).getAllByRole("button")) {
      expect((button as HTMLButtonElement).disabled).toBe(true);
    }
    expect((clearAll() as HTMLButtonElement | null)?.disabled ?? true).toBe(true);
    pending.resolve();
    await waitFor(() => expect(screen.getByRole("status")).toBeTruthy());
  });

  test("a failure shows the server's message, or a fallback when there is none", async () => {
    const clear = vi
      .spyOn(services.analyticsService, "clear")
      .mockRejectedValueOnce(new Error("Database is read-only"))
      .mockRejectedValueOnce(new Error(""));
    render(<AnalyticsClearSettings />);

    fireEvent.click(clearAll());
    fireEvent.click(
      within(screen.getByRole("group")).getByRole("button", { name: /confirm|clear/i }),
    );
    expect((await screen.findByRole("alert")).textContent).toBe("Database is read-only");

    fireEvent.click(clearAll());
    fireEvent.click(
      within(screen.getByRole("group")).getByRole("button", { name: /confirm|clear/i }),
    );
    await waitFor(() =>
      expect(screen.getByRole("alert").textContent).toBe("Could not clear analytics."),
    );
    expect(clear).toHaveBeenCalledTimes(2);
  });
});
