/** How long a saved file's object URL stays alive. */
export const REVOKE_DELAY_MS = 40_000;

/** Hands bytes the page holds to the browser to save under a name. */
export function saveBlob(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  // Not inside the click, and not soon after: some browsers start the save once it has returned, and a large
  // file is still being written out long after. Held for as long as FileSaver.js holds it.
  setTimeout(() => URL.revokeObjectURL(url), REVOKE_DELAY_MS);
}
