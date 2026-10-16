import type { LoopTemplateChanges, LoopTemplateProposalEdgeRef } from "../types";

/** A config field as a person reads it: "config.command" is "command". */
function fieldName(field: string) {
  return field.startsWith("config.") ? field.slice("config.".length) : field;
}

function edgeText(edge: LoopTemplateProposalEdgeRef) {
  return `${edge.sourceLabel} → ${edge.targetLabel} (${edge.name ?? edge.edgeType})`;
}

/**
 * What a loop template proposal changes against its base version, as a list:
 * nodes added, removed or changed (naming the fields), edges added or removed,
 * and the template's own name, description or recovery policy. Never the
 * document itself.
 */
export default function LoopTemplateChangeList({
  changes,
}: {
  changes: LoopTemplateChanges | null;
}) {
  if (!changes)
    return <p className="lt-changes-none">The version it was made against no longer exists.</p>;

  const items: { key: string; kind: "added" | "removed" | "changed"; text: string }[] = [];
  if (changes.nameChanged) items.push({ key: "name", kind: "changed", text: "Template name" });
  if (changes.descriptionChanged)
    items.push({ key: "description", kind: "changed", text: "Template description" });
  if (changes.recoveryPolicyChanged)
    items.push({ key: "policy", kind: "changed", text: "Recovery policy" });
  for (const n of changes.nodesAdded)
    items.push({ key: `na:${n.id}`, kind: "added", text: `Node ${n.label} (${n.type})` });
  for (const n of changes.nodesRemoved)
    items.push({ key: `nr:${n.id}`, kind: "removed", text: `Node ${n.label} (${n.type})` });
  for (const n of changes.nodesChanged)
    items.push({
      key: `nc:${n.id}`,
      kind: "changed",
      text: `Node ${n.label} (${n.type}): ${n.fields.map(fieldName).join(", ")}`,
    });
  changes.edgesAdded.forEach((e, i) =>
    items.push({ key: `ea:${i}`, kind: "added", text: `Edge ${edgeText(e)}` }),
  );
  changes.edgesRemoved.forEach((e, i) =>
    items.push({ key: `er:${i}`, kind: "removed", text: `Edge ${edgeText(e)}` }),
  );

  if (items.length === 0) return <p className="lt-changes-none">No changes to the saved loop.</p>;
  return (
    <ul className="lt-changes" aria-label="Proposed changes">
      {items.map((item) => (
        <li key={item.key} className={`lt-change lt-change--${item.kind}`}>
          <span className="lt-change-kind">{item.kind}</span> {item.text}
        </li>
      ))}
    </ul>
  );
}
