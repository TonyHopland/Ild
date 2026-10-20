import { useEffect, useRef, useState, type CSSProperties, type KeyboardEvent } from "react";
import Icon from "./Icon";

/**
 * An answer waiting on the person to confirm it: the button they pressed and
 * its colour, what it sends, and the `needsConfirm` it was asked under.
 */
interface PendingAnswer {
  label: string;
  tone: string;
  style: CSSProperties | undefined;
  send: () => void;
  askedUnder: (name: string) => boolean;
}

interface FeedbackActionsProps {
  actions: string | null | undefined;
  onApprove: () => void;
  onReject: () => void;
  onEdge: (name: string) => void;
  /**
   * When provided, a Merge button is shown that performs the real remote merge
   * (and optional branch delete) before the loop continues along OnSuccess.
   * Only wired in for PR-awaiting-merge feedback.
   */
  onMerge?: (deleteBranch: boolean) => void;
  /** An answer is in flight; pressing again would submit it a second time. */
  busy?: boolean;
  /** Whether the parked node offers the output of that name to the person answering. */
  isVisible?: (name: string) => boolean;
  /**
   * Whether the parked node asks the person answering to confirm before taking
   * the output of that name. A new function means a new state of the parked
   * node: a confirmation still open from before it is dropped unsent.
   */
  needsConfirm?: (name: string) => boolean;
  /** The "#rrggbb" colour of the button for the output of that name, or null for its tone. */
  colorOf?: (name: string) => string | null;
  /** The feedback box, rendered above the buttons; what it holds is sent with the answer. */
  input?: {
    value: string;
    onChange: (value: string) => void;
    placeholder: string;
    rows: number;
  };
  /** The parked node's default output, or null when it has none or it is not known yet. */
  defaultOutput?: string | null;
  /** Whether Enter in the feedback box takes the default output, as its button would. */
  submitDefaultOnEnter?: boolean;
}

// Tokens in the comma-separated actions string that map to the fixed
// success/failure roles; everything else names one of the node's outputs.
const ROLE_TOKENS = new Set(["OnSuccess", "OnFailure"]);

/** A button's look: one primary, Reject in danger, everything else secondary. */
type Tone = "btn-primary" | "btn-secondary" | "btn-danger";

/**
 * Renders the node's answers as buttons, from the comma-separated
 * <c>humanFeedbackActions</c> on the work item: Approve (OnSuccess), Merge, one
 * per named output, and Reject (OnFailure). Defaults to Approve + Reject when
 * empty. An output the node hides has no button.
 *
 * However many there are, one is primary: the node's default output, else
 * Merge, else Approve, else the first named output in node order. The default comes first and Reject last. A colour set on
 * an output in the loop editor shows as a dot and border, so the primary stays
 * the obvious choice. An output that asks to confirm is marked "…" and, when
 * pressed, swaps the buttons for an inline confirmation.
 * Merge is not an output and is always offered, behind its own confirmation.
 * With submitDefaultOnEnter, Enter in the feedback box presses the default
 * output's button, which is marked ⏎; Shift+Enter still adds a new line.
 */
export default function FeedbackActions({
  actions,
  onApprove,
  onReject,
  onEdge,
  onMerge,
  busy = false,
  isVisible = () => true,
  needsConfirm,
  colorOf,
  input,
  defaultOutput = null,
  submitDefaultOnEnter = false,
}: FeedbackActionsProps) {
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const confirmRef = useRef<HTMLButtonElement>(null);
  const [asked, setAsked] = useState<PendingAnswer | null>(null);
  // The answer was for the node as it was then; confirming it now could send it to another one.
  const pending = asked?.askedUnder === needsConfirm ? asked : null;
  const [confirmingMerge, setConfirmingMerge] = useState(false);
  const [deleteBranch, setDeleteBranch] = useState(true);

  const actionList = (
    actions
      ? actions
          .split(",")
          .map((a) => a.trim())
          .filter(Boolean)
      : ["OnSuccess", "OnFailure"]
  ).filter((name) => isVisible(name));

  const customNames = actionList.filter((a) => !ROLE_TOKENS.has(a));

  // Only an output with a button: a default that is hidden or has no edge is not offered.
  const shownDefault =
    defaultOutput !== null && actionList.includes(defaultOutput) ? defaultOutput : null;
  const enterTarget = submitDefaultOnEnter ? shownDefault : null;
  const primary: string | null =
    shownDefault ??
    (onMerge ? "Merge" : actionList.includes("OnSuccess") ? "OnSuccess" : (customNames[0] ?? null));

  const toneOf = (name: string): Tone =>
    name === primary ? "btn-primary" : name === "OnFailure" ? "btn-danger" : "btn-secondary";

  const styleOf = (name: string): CSSProperties | undefined => {
    const color = colorOf?.(name);
    return color ? ({ "--output-color": color } as CSSProperties) : undefined;
  };

  const labelOf = (name: string) =>
    name === "OnSuccess" ? "Approve" : name === "OnFailure" ? "Reject" : name;

  const sendOf = (name: string) =>
    name === "OnSuccess" ? onApprove : name === "OnFailure" ? onReject : () => onEdge(name);

  const answerFor = (name: string) => () => {
    const send = sendOf(name);
    if (needsConfirm?.(name))
      setAsked({
        label: labelOf(name),
        tone: name === "OnFailure" ? "btn-danger" : "btn-primary",
        style: styleOf(name),
        send,
        askedUnder: needsConfirm,
      });
    else send();
  };

  const onInputKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    // Safari ends a composition before the Enter that commits it, which then
    // arrives with isComposing false and only keyCode 229 to tell it apart:
    // https://developer.mozilla.org/en-US/docs/Web/API/Element/keydown_event#keydown_events_with_ime
    const composing = e.nativeEvent.isComposing || e.nativeEvent.keyCode === 229;
    const plainEnter = e.key === "Enter" && !e.shiftKey && !composing && !e.repeat;
    if (!plainEnter || busy || enterTarget === null) return;
    e.preventDefault();
    answerFor(enterTarget)();
  };

  const closeConfirmation = () => {
    setAsked(null);
    inputRef.current?.focus();
  };

  // The confirmation takes focus, so Enter confirms (a held-down Enter does not),
  // and Escape cancels it alone, before the dialog around it sees the key.
  const confirmOpen = pending !== null;
  useEffect(() => {
    if (!confirmOpen) return;
    confirmRef.current?.focus();
    const onKey = (e: globalThis.KeyboardEvent) => {
      if (e.key !== "Escape") return;
      e.stopPropagation();
      e.preventDefault();
      setAsked(null);
      inputRef.current?.focus();
    };
    window.addEventListener("keydown", onKey, true);
    return () => window.removeEventListener("keydown", onKey, true);
  }, [confirmOpen]);

  // Default first, then Approve, Merge and the named outputs in node order, Reject last.
  const order = [
    ...(shownDefault ? [shownDefault] : []),
    ...(actionList.includes("OnSuccess") && shownDefault !== "OnSuccess" ? ["OnSuccess"] : []),
    ...(onMerge ? ["Merge"] : []),
    ...customNames.filter((name) => name !== shownDefault),
    ...(actionList.includes("OnFailure") && shownDefault !== "OnFailure" ? ["OnFailure"] : []),
  ];

  const button = (name: string) => {
    if (name === "Merge")
      return (
        <button
          key="Merge"
          type="button"
          className={`btn btn-sm ${toneOf("Merge")}`}
          onClick={() => setConfirmingMerge(true)}
        >
          Merge
        </button>
      );
    const style = styleOf(name);
    return (
      <button
        key={name}
        type="button"
        className={`btn btn-sm ${toneOf(name)}${style ? " has-output-color" : ""}`}
        style={style}
        onClick={answerFor(name)}
        disabled={busy}
        aria-keyshortcuts={enterTarget === name ? "Enter" : undefined}
      >
        {style && <span className="output-color-dot" aria-hidden="true" />}
        {labelOf(name)}
        {needsConfirm?.(name) && (
          <span className="feedback-confirm-mark" aria-hidden="true">
            …
          </span>
        )}
        {name === enterTarget && (
          <kbd className="feedback-enter-mark" aria-hidden="true">
            ⏎
          </kbd>
        )}
      </button>
    );
  };

  return (
    <>
      {input && (
        <textarea
          ref={inputRef}
          className="feedback-textarea"
          value={input.value}
          onChange={(e) => input.onChange(e.target.value)}
          onKeyDown={onInputKeyDown}
          placeholder={input.placeholder}
          rows={input.rows}
        />
      )}
      {pending ? (
        <div className="feedback-confirm" role="dialog" aria-label={`Confirm ${pending.label}`}>
          <p className="feedback-confirm-text">
            <Icon name="alert" />
            <strong>{pending.label}?</strong> The run moves on as soon as you confirm.
          </p>
          <div className="feedback-actions">
            <button
              ref={confirmRef}
              type="button"
              className={`btn ${pending.tone}${pending.style ? " has-output-color" : ""}`}
              style={pending.style}
              // Enter confirms once; a held-down Enter does not.
              onKeyDown={(e) => {
                if (e.key !== "Enter") return;
                e.preventDefault();
                if (e.repeat) return;
                closeConfirmation();
                pending.send();
              }}
              onClick={() => {
                closeConfirmation();
                pending.send();
              }}
            >
              {pending.style && <span className="output-color-dot" aria-hidden="true" />}
              {pending.label}
            </button>
            <button type="button" className="btn btn-secondary" onClick={closeConfirmation}>
              Cancel
            </button>
          </div>
        </div>
      ) : (
        <div className="feedback-actions">
          {order.map(button)}
          {onMerge && confirmingMerge && (
            <div className="merge-confirm" role="dialog" aria-label="Confirm merge">
              <label className="merge-confirm-option">
                <input
                  type="checkbox"
                  checked={deleteBranch}
                  onChange={(e) => setDeleteBranch(e.target.checked)}
                />
                Delete branch after merge
              </label>
              <div className="merge-confirm-actions">
                <button
                  type="button"
                  className="btn btn-sm btn-primary"
                  onClick={() => {
                    setConfirmingMerge(false);
                    onMerge(deleteBranch);
                  }}
                >
                  Confirm Merge
                </button>
                <button
                  type="button"
                  className="btn btn-sm btn-secondary"
                  onClick={() => setConfirmingMerge(false)}
                >
                  Cancel
                </button>
              </div>
            </div>
          )}
        </div>
      )}
    </>
  );
}
