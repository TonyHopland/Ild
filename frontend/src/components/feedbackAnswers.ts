import type { CSSProperties } from "react";

// How the feedback buttons are chosen, ordered and coloured: plain rules kept
// out of FeedbackActions, so they can be read and tested on their own.

/** The label of the button for an output: the role's verb for the two defaults, otherwise the output's own name. */
export const outputLabel = (name: string) =>
  name === "OnSuccess" ? "Approve" : name === "OnFailure" ? "Reject" : name;

// The PR merge in the list of buttons. It holds a comma, which no output name
// can, as the actions string is split on commas: an output called "Merge" is
// its own button.
export const PR_MERGE = ",pr-merge";

/** A button's look: one primary, Reject in danger, everything else secondary. */
export type Tone = "btn-primary" | "btn-secondary" | "btn-danger";

/** The outputs listed in the actions string, in order, that the node shows. Nothing listed means Approve and Reject. */
export function visibleAnswersOf(
  actions: string | null | undefined,
  isVisible: (name: string) => boolean,
): string[] {
  return (
    actions
      ? actions
          .split(",")
          .map((a) => a.trim())
          .filter(Boolean)
      : ["OnSuccess", "OnFailure"]
  ).filter((name) => isVisible(name));
}

/** The one answer pushed over the others: the shown default, else the PR merge when offered. */
export function primaryAnswerOf(shownDefault: string | null, withMerge: boolean): string | null {
  return shownDefault ?? (withMerge ? PR_MERGE : null);
}

export function answerToneOf(name: string, primary: string | null): Tone {
  return name === primary ? "btn-primary" : name === "OnFailure" ? "btn-danger" : "btn-secondary";
}

/** The confirm button of an answer that asks first: the one choice left, so primary, unless it rejects. */
export function confirmToneOf(name: string): Tone {
  return name === "OnFailure" ? "btn-danger" : "btn-primary";
}

/** The button style that hands an output colour to the stylesheet, or none when it has no colour. */
export function answerStyleOf(color: string | null | undefined): CSSProperties | undefined {
  return color ? ({ "--output-color": color } as CSSProperties) : undefined;
}
