import { describe, expect, test } from "vite-plus/test";
import { makeLoopTagMatcher } from "../workItemJson";

describe("makeLoopTagMatcher", () => {
  test("matches a tag that names a loop template", () => {
    const isLoopTag = makeLoopTagMatcher(["Bug Fix", "Feature"]);
    expect(isLoopTag("Bug Fix")).toBe(true);
    expect(isLoopTag("Feature")).toBe(true);
  });

  test("does not match a free-form tag", () => {
    const isLoopTag = makeLoopTagMatcher(["Bug Fix"]);
    expect(isLoopTag("urgent")).toBe(false);
  });

  test("matches case-insensitively, mirroring the backend resolver", () => {
    const isLoopTag = makeLoopTagMatcher(["Bug Fix"]);
    expect(isLoopTag("bug fix")).toBe(true);
    expect(isLoopTag("BUG FIX")).toBe(true);
  });

  test("never matches when there are no loop templates", () => {
    const isLoopTag = makeLoopTagMatcher([]);
    expect(isLoopTag("Bug Fix")).toBe(false);
  });
});
