import type { DiffLine } from "../utils/jsonDiff";

/**
 * Renders a line diff with the save-diff-* classes. A changed line is shaded
 * lightly as a whole and, where the diff worked it out, the words that differ
 * from its counterpart strongly.
 */
export default function LineDiffView({ diff, testId }: { diff: DiffLine[]; testId: string }) {
  return (
    <div className="save-diff-view" data-testid={testId}>
      {diff.map((line, idx) => (
        <div key={idx} className={`save-diff-line save-diff-${line.type}`}>
          <span className="save-diff-gutter">
            {line.type === "add" ? "+" : line.type === "del" ? "-" : " "}
          </span>
          <span className="save-diff-text">
            {line.segments
              ? line.segments.map((segment, segIdx) => (
                  <span
                    key={segIdx}
                    className={segment.changed ? `save-diff-seg-${line.type}` : undefined}
                  >
                    {segment.text}
                  </span>
                ))
              : line.text}
          </span>
        </div>
      ))}
    </div>
  );
}
