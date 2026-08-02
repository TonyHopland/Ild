import { describe, expect, test } from "vite-plus/test";
import { isTemplatedSessionName, sessionPlaceholderError } from "./sessionPlaceholder";

describe("isTemplatedSessionName", () => {
  test("a literal name is not templated", () => {
    expect(isTemplatedSessionName("research")).toBe(false);
    expect(isTemplatedSessionName("")).toBe(false);
  });

  test("an interpolated name is templated", () => {
    expect(isTemplatedSessionName("ticket_{{Var.current_ticket}}")).toBe(true);
    expect(isTemplatedSessionName("{{ Var.n }}")).toBe(true);
  });
});

describe("sessionPlaceholderError", () => {
  test("accepts a literal name", () => {
    expect(sessionPlaceholderError("Session name", "research")).toBeNull();
  });

  test("accepts a loop variable", () => {
    expect(sessionPlaceholderError("Session name", "ticket_{{Var.current_ticket}}")).toBeNull();
  });

  test.each([
    "{{PreviousNode.Output}}",
    "t_{{WorkItem.Title}}",
    "t_{{Node.Input}}",
    "t_{{Bogus.Thing}}",
    "t_{{Var.9bad}}",
  ])("rejects %s", (value) => {
    const error = sessionPlaceholderError("Session name", value);
    expect(error).toContain("Session name");
    expect(error).toContain("Var.");
  });

  test("names the field it was given", () => {
    expect(sessionPlaceholderError("Fork from", "{{PreviousNode.Output}}")).toContain("Fork from");
  });
});
