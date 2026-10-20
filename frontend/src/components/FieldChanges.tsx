import { useMemo } from "react";
import { computeLineDiff } from "../utils/jsonDiff";
import "./FieldChanges.css";

/** A text change as a unified line diff: unchanged lines once, the changed ones as removed and added lines. */
export function DescriptionDiff({ before, after }: { before: string; after: string }) {
  const diff = useMemo(() => computeLineDiff(before, after), [before, after]);
  return (
    <div className="edit-proposal-diff">
      {diff.map((line, idx) => (
        <div key={idx} className={`edit-proposal-diff-line edit-proposal-diff-${line.type}`}>
          <span className="edit-proposal-diff-gutter" aria-hidden="true">
            {line.type === "add" ? "+" : line.type === "del" ? "-" : " "}
          </span>
          <span className="edit-proposal-diff-text">{line.text}</span>
        </div>
      ))}
    </div>
  );
}

/** A list change as chips: the added and the removed ones marked, the kept ones plain. */
export function TagsChange({ before, after }: { before: string[]; after: string[] }) {
  const removed = before.filter((t) => !after.includes(t));
  const added = after.filter((t) => !before.includes(t));
  const kept = after.filter((t) => before.includes(t));
  return (
    <div className="edit-proposal-tags">
      {added.map((t) => (
        <span key={`+${t}`} className="edit-proposal-tag edit-proposal-tag--added" title="Added">
          +{t}
        </span>
      ))}
      {removed.map((t) => (
        <span
          key={`-${t}`}
          className="edit-proposal-tag edit-proposal-tag--removed"
          title="Removed"
        >
          −{t}
        </span>
      ))}
      {kept.map((t) => (
        <span key={`=${t}`} className="edit-proposal-tag">
          {t}
        </span>
      ))}
    </div>
  );
}
