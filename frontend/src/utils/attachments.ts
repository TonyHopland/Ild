const KILOBYTE = 1024;
const MEGABYTE = 1024 * KILOBYTE;

const trim = (value: number) => (Number.isInteger(value) ? String(value) : value.toFixed(1));

/** A byte count in the unit a human reads it in, e.g. `25 MB`. */
export function formatBytes(bytes: number): string {
  if (bytes >= MEGABYTE) return `${trim(bytes / MEGABYTE)} MB`;
  if (bytes >= KILOBYTE) return `${trim(bytes / KILOBYTE)} KB`;
  return `${bytes} bytes`;
}

/**
 * Refusal for a file the instance's per-file maximum rules out. Worded as the
 * API words its own refusal, and names the limit: "too large" on its own leaves
 * the human guessing which file to shrink and by how much.
 */
export function oversizeMessage(fileName: string, maxBytesPerFile: number): string {
  return `'${fileName}' is larger than the ${formatBytes(maxBytesPerFile)} allowed per file.`;
}

/**
 * The answer recorded in the conversation: what the human typed, then the names
 * of the files now on the work item. The agent reads this verbatim, so the names
 * are how it learns the attachments are there at all.
 */
export function attachedNote(typed: string, fileNames: string[]): string {
  if (fileNames.length === 0) return typed;
  const list = `Attached files: ${fileNames.join(", ")}`;
  const text = typed.trim();
  return text ? `${text}\n\n${list}` : list;
}

const pad = (value: number) => String(value).padStart(2, "0");

/**
 * The name a file is staged under. A pasted screenshot can arrive nameless, and
 * the server's own fallback is the literal "attachment" — which would make every
 * pasted image on an item indistinguishable from the next.
 */
export function stagedFileName(file: File, now: Date): string {
  if (file.name) return file.name;
  const stamp =
    `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}` +
    `-${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`;
  const subtype = file.type.split("/")[1]?.split("+")[0];
  return `pasted-${stamp}.${subtype || "bin"}`;
}
