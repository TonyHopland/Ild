/** Hands bytes the page holds to the browser to save under a name. */
export function saveBlob(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  // Some browsers start the save after the click returns, and would find the bytes gone.
  setTimeout(() => URL.revokeObjectURL(url), 30_000);
}
