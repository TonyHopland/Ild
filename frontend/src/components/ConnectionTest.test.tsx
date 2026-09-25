import { describe, expect, test } from "vite-plus/test";
import { render, screen, fireEvent } from "@testing-library/react";
import ConnectionTest from "./ConnectionTest";
import { ConnectionTestResult } from "../types";

function runWith(run: () => Promise<ConnectionTestResult>) {
  render(<ConnectionTest run={run} />);
  fireEvent.click(screen.getByRole("button", { name: "Test" }));
}

describe("ConnectionTest announces what the test found", () => {
  test("a passing test is a polite status", async () => {
    runWith(async () => ({ ok: true, outcome: "Ok", message: "Signed in.", detail: null }));

    expect((await screen.findByRole("status")).textContent).toContain("Signed in.");
    expect(screen.queryByRole("alert")).toBeNull();
  });

  test("a failing test is an alert", async () => {
    runWith(async () => ({
      ok: false,
      outcome: "InvalidApiKey",
      message: "The key was rejected.",
      detail: "HTTP 401",
    }));

    expect((await screen.findByRole("alert")).textContent).toContain("The key was rejected.");
    expect(screen.queryByRole("status")).toBeNull();
  });

  test("a test that could not run is an alert", async () => {
    runWith(async () => {
      throw new Error("Network down");
    });

    expect((await screen.findByRole("alert")).textContent).toBe(
      "Couldn't run the test: Network down",
    );
  });
});
