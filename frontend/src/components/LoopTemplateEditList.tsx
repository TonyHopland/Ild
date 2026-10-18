import { useMemo, useState } from "react";
import type { LoopTemplateEdit, LoopTemplateProposal } from "../types";

function EditText({ label, text }: { label: string; text: string }) {
  return (
    <div className="lt-edit-text">
      <span className="lt-edit-text-label">{label}</span>
      <pre aria-label={label}>{text}</pre>
    </div>
  );
}

const COLLAPSED_LINES = 8;
const COLLAPSED_CHARS = 600;

/** The start of a long text, or null when it is short enough to show whole. */
function collapsed(text: string): string | null {
  const head = text.split("\n").slice(0, COLLAPSED_LINES).join("\n").slice(0, COLLAPSED_CHARS);
  return head.length < text.length ? `${head}…` : null;
}

function CollapsibleText({ label, text }: { label: string; text: string }) {
  const [whole, setWhole] = useState(false);
  const head = collapsed(text);
  return (
    <div className="lt-edit-text">
      <span className="lt-edit-text-label">{label}</span>
      <pre aria-label={label}>{whole || head === null ? text : head}</pre>
      {head !== null && (
        <button type="button" className="lt-edit-expand" onClick={() => setWhole((w) => !w)}>
          {whole ? "Collapse value" : "Show whole value"}
        </button>
      )}
    </div>
  );
}

/** Node labels by id from the resulting document; none when it is not loaded or not readable. */
function nodeLabels(document: string | null | undefined): Map<string, string> {
  const labels = new Map<string, string>();
  if (!document) return labels;
  try {
    const nodes: unknown = (JSON.parse(document) as { nodes?: unknown }).nodes;
    if (!Array.isArray(nodes)) return labels;
    for (const node of nodes as { id?: unknown; label?: unknown }[])
      if (typeof node?.id === "string" && typeof node.label === "string" && node.label)
        labels.set(node.id, node.label);
  } catch {
    // An unreadable document names no nodes; the ids as sent stand.
  }
  return labels;
}

function EditRow({ edit, labels }: { edit: LoopTemplateEdit; labels: Map<string, string> }) {
  if (edit.op === "editFile")
    return (
      <li className="lt-edit">
        <div className="lt-edit-target">Document JSON</div>
        <EditText label="Before" text={edit.oldString} />
        <EditText label="After" text={edit.newString} />
      </li>
    );
  const target = (
    <div className="lt-edit-target">
      <span className="lt-edit-node">{labels.get(edit.nodeId) ?? edit.nodeId}</span>
      {" · "}
      <span className="lt-edit-field">{edit.field}</span>
    </div>
  );
  if (edit.op === "setNodeField")
    return (
      <li className="lt-edit">
        {target}
        <div className="lt-edit-note">replaced whole field</div>
        <CollapsibleText label="Value" text={edit.value} />
      </li>
    );
  return (
    <li className="lt-edit">
      {target}
      <EditText label="Before" text={edit.oldString} />
      <EditText label="After" text={edit.newString} />
    </li>
  );
}

/**
 * The Edit List a loop template proposal was made from: how many edits, and on
 * request each edit in order, showing only what the agent sent. Nothing for a
 * whole-document proposal.
 */
export default function LoopTemplateEditList({
  proposal,
}: {
  proposal: Pick<LoopTemplateProposal, "edits" | "document">;
}) {
  const [open, setOpen] = useState(false);
  const { edits, document } = proposal;
  const labels = useMemo(() => nodeLabels(document), [document]);
  if (!edits) return null;

  return (
    <div className="lt-edits">
      <div className="lt-edits-header">
        <span className="lt-edits-count">
          {edits.length} {edits.length === 1 ? "edit" : "edits"}
        </span>
        <button
          type="button"
          className="btn btn-secondary btn-sm"
          aria-expanded={open}
          onClick={() => setOpen((o) => !o)}
        >
          {open ? "Hide edits" : "Show edits"}
        </button>
      </div>
      {open && (
        <ol className="lt-edit-rows" aria-label="Edits">
          {edits.map((edit, i) => (
            <EditRow key={i} edit={edit} labels={labels} />
          ))}
        </ol>
      )}
    </div>
  );
}
