/**
 * Save an attachment the user clicked. Attachments sit behind the bearer token,
 * so they cannot be a plain href: the bytes are fetched, handed to a temporary
 * object URL, and the URL is released once the browser has had a chance to act
 * on it.
 *
 * The release is deferred rather than run straight after `click()`. Chromium
 * happens to snapshot the URL synchronously, but that is not guaranteed —
 * revoking in the same task can leave Firefox and Safari with a dead URL and no
 * download. Deferring costs nothing and does not depend on which engine is
 * reading it.
 */
export async function downloadAttachment(fetchBlob: () => Promise<Blob>, fileName: string) {
  const url = URL.createObjectURL(await fetchBlob());
  try {
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    link.rel = "noopener";
    link.click();
  } finally {
    setTimeout(() => URL.revokeObjectURL(url), 0);
  }
}

/** Human-readable size for an attachment chip. */
export function formatBytes(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  if (bytes >= 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${bytes} B`;
}

/**
 * The upload limits, as the browser knows them. The server enforces its own —
 * these exist so an oversized pick is refused before it is sent, and they mirror
 * `AttachmentIntake`. This is the only place the numbers are written on this side
 * of the wire; every surface that stages files imports them from here.
 */
export const MAX_ATTACHMENT_MB = 25;
export const MAX_ATTACHMENT_BYTES = MAX_ATTACHMENT_MB * 1024 * 1024;
export const MAX_ATTACHMENTS_PER_MESSAGE = 10;
