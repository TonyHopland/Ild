import { useRef, useState, type CSSProperties, type KeyboardEvent } from "react";
import ConfirmModal from "./ConfirmModal";
import { readableTextOn } from "../utils/nodeOutputs";

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

const APPROVE_TONE = "btn-primary";
const OUTPUT_TONE = "btn-warning";
const REJECT_TONE = "btn-danger";

/**
 * Renders the Approve / Merge / named-output / Reject buttons based on the
 * comma-separated <c>humanFeedbackActions</c> string from the work item.
 * Each wired named output surfaces as its own button (its name is the output
 * sent back to the engine). Defaults to Approve + Reject when empty. An output
 * the node hides has no button, one it marks for confirmation asks first, and
 * one it colours has its button in that colour.
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

  const styleOf = (name: string): CSSProperties | undefined => {
    const color = colorOf?.(name);
    return color
      ? { backgroundColor: color, borderColor: color, color: readableTextOn(color) }
      : undefined;
  };

  const answer = (name: string, label: string, tone: string, send: () => void) => () => {
    if (needsConfirm?.(name))
      setAsked({ label, tone, style: styleOf(name), send, askedUnder: needsConfirm });
    else send();
  };

  const answerFor = (name: string) => {
    if (name === "OnSuccess") return answer(name, "Approve", APPROVE_TONE, onApprove);
    if (name === "OnFailure") return answer(name, "Reject", REJECT_TONE, onReject);
    return answer(name, name, OUTPUT_TONE, () => onEdge(name));
  };

  // Only an output with a button: a default that is hidden or has no edge is not offered.
  const enterTarget =
    submitDefaultOnEnter && defaultOutput !== null && actionList.includes(defaultOutput)
      ? defaultOutput
      : null;

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

  const enterMarker = (name: string) => name === enterTarget && <span aria-hidden="true"> ⏎</span>;

  const closeConfirmation = () => {
    setAsked(null);
    inputRef.current?.focus();
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
      <div className="feedback-actions">
        {actionList.includes("OnSuccess") && (
          <button
            type="button"
            className={`btn btn-sm ${APPROVE_TONE}`}
            style={styleOf("OnSuccess")}
            onClick={answerFor("OnSuccess")}
            disabled={busy}
            aria-keyshortcuts={enterTarget === "OnSuccess" ? "Enter" : undefined}
          >
            Approve
            {enterMarker("OnSuccess")}
          </button>
        )}
        {onMerge && (
          <button
            type="button"
            className="btn btn-sm btn-success"
            onClick={() => setConfirmingMerge(true)}
          >
            Merge
          </button>
        )}
        {customNames.map((name) => (
          <button
            key={name}
            type="button"
            className={`btn btn-sm ${OUTPUT_TONE}`}
            style={styleOf(name)}
            onClick={answerFor(name)}
            disabled={busy}
            aria-keyshortcuts={enterTarget === name ? "Enter" : undefined}
          >
            {name}
            {enterMarker(name)}
          </button>
        ))}
        {actionList.includes("OnFailure") && (
          <button
            type="button"
            className={`btn btn-sm ${REJECT_TONE}`}
            style={styleOf("OnFailure")}
            onClick={answerFor("OnFailure")}
            disabled={busy}
            aria-keyshortcuts={enterTarget === "OnFailure" ? "Enter" : undefined}
          >
            Reject
            {enterMarker("OnFailure")}
          </button>
        )}
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
                className="btn btn-sm btn-success"
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
        {pending && (
          <ConfirmModal
            isOpen
            title={`Confirm ${pending.label}`}
            message={`Are you sure you want to take "${pending.label}"? The run moves on as soon as you confirm.`}
            confirmText={pending.label}
            confirmClassName={pending.tone}
            confirmStyle={pending.style}
            focusConfirm
            onConfirm={() => {
              closeConfirmation();
              pending.send();
            }}
            onCancel={closeConfirmation}
          />
        )}
      </div>
    </>
  );
}
