/**
 * The link that opens VS Code attached to a running container (Dev Containers' "Attach to Running Container"), with
 * `folder` open. VS Code names the container by the hex of its JSON description, and the name keeps its leading slash.
 */
export function vscodeAttachUri(containerName: string, folder: string): string {
  const description = JSON.stringify({ containerName: `/${containerName.replace(/^\//, "")}` });
  const hex = Array.from(new TextEncoder().encode(description), (b) =>
    b.toString(16).padStart(2, "0"),
  ).join("");
  return `vscode://vscode-remote/attached-container+${hex}${encodeURI(folder)}`;
}

/** Hands a link for another application (such as `vscode://`) to the browser, which asks the OS to open it. */
export function openAppLink(uri: string): void {
  window.location.href = uri;
}
