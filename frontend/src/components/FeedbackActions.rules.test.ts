import { describe, expect, test } from "vite-plus/test";
import {
  PR_MERGE,
  answerStyleOf,
  answerToneOf,
  primaryAnswerOf,
  visibleAnswersOf,
} from "./FeedbackActions";

describe("visibleAnswersOf: the listed outputs the node shows", () => {
  const all = () => true;

  test.each([
    [
      "listed outputs keep their order",
      "OnSuccess,Needs work,OnFailure",
      all,
      ["OnSuccess", "Needs work", "OnFailure"],
    ],
    [
      "blank entries and padding are dropped",
      " OnSuccess , ,OnFailure ",
      all,
      ["OnSuccess", "OnFailure"],
    ],
    ["nothing listed falls back to Approve and Reject", null, all, ["OnSuccess", "OnFailure"]],
    ["empty actions fall back to Approve and Reject", "", all, ["OnSuccess", "OnFailure"]],
    [
      "a hidden success output has no entry",
      "OnSuccess,Needs work,Escalate,OnFailure",
      (n: string) => n !== "OnSuccess" && n !== "Escalate",
      ["Needs work", "OnFailure"],
    ],
    [
      "a hidden failure output has no entry",
      "OnSuccess,OnFailure",
      (n: string) => n !== "OnFailure",
      ["OnSuccess"],
    ],
    [
      "the fallback drops a hidden success output too",
      null,
      (n: string) => n !== "OnSuccess",
      ["OnFailure"],
    ],
    [
      "every listed output hidden leaves none, without falling back",
      "OnSuccess,OnFailure,later",
      () => false,
      [],
    ],
    ["the fallback with both hidden leaves none", undefined, () => false, []],
  ])("%s", (_, actions, isVisible, expected) => {
    expect(visibleAnswersOf(actions, isVisible)).toEqual(expected);
  });
});

describe("primaryAnswerOf: the one answer pushed over the others", () => {
  test.each([
    ["a shown default wins over Merge", "on_merged", true, "on_merged"],
    ["a shown default, no Merge", "OnSuccess", false, "OnSuccess"],
    ["no default, Merge offered: Merge", null, true, PR_MERGE],
    ["no default, no Merge: none", null, false, null],
  ])("%s", (_, shownDefault, withMerge, primary) => {
    expect(primaryAnswerOf(shownDefault, withMerge)).toBe(primary);
  });
});

describe("answerToneOf: how an answer's button looks", () => {
  test.each([
    ["the primary answer", "on_merged", "on_merged", "btn-primary"],
    ["the primary Reject stays primary", "OnFailure", "OnFailure", "btn-primary"],
    ["Reject when it is not primary", "OnFailure", "OnSuccess", "btn-danger"],
    ["Reject with no primary at all", "OnFailure", null, "btn-danger"],
    ["Approve when it is not primary", "OnSuccess", "on_merged", "btn-secondary"],
    ["a named output when it is not primary", "Needs work", null, "btn-secondary"],
    ["Merge when it is primary", PR_MERGE, PR_MERGE, "btn-primary"],
    ["Merge when another answer is primary", PR_MERGE, "Needs work", "btn-secondary"],
  ])("%s", (_, name, primary, tone) => {
    expect(answerToneOf(name, primary)).toBe(tone);
  });
});

describe("answerStyleOf: the colour a button carries", () => {
  test("a colour is handed to the button as its output colour", () => {
    expect(answerStyleOf("#ff0000")).toEqual({ "--output-color": "#ff0000" });
  });

  test.each([[null], [undefined], [""]])("%s leaves the button to its tone", (color) => {
    expect(answerStyleOf(color)).toBeUndefined();
  });
});
