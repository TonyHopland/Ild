import { describe, expect, test } from "vite-plus/test";
import { makeLoopTagMatcher } from "../workItemJson";

describe("makeLoopTagMatcher", () => {
  test.each([
    [["Bug Fix", "Feature"], "Bug Fix", true],
    [["Bug Fix", "Feature"], "Feature", true],
    [["Bug Fix"], "urgent", false],
    [["Bug Fix"], "bug fix", true],
    [["Bug Fix"], "BUG FIX", true],
    [[], "Bug Fix", false],
  ])("with templates %j, the tag %s is a loop tag: %s", (templates, tag, expected) => {
    expect(makeLoopTagMatcher(templates)(tag)).toBe(expected);
  });
});
