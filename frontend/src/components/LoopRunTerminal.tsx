import TerminalView from "./TerminalView";

const API_BASE: string = (import.meta.env?.VITE_API_BASE as string | undefined) ?? "/api/v1";

interface Props {
  loopRunId: string;
  title: string;
  onClose: () => void;
}

function buildWebSocketUrl(loopRunId: string, cols: number, rows: number): string {
  const apiBase = API_BASE.startsWith("http") ? API_BASE : `${window.location.origin}${API_BASE}`;
  const url = new URL(`${apiBase}/loopruns/${loopRunId}/terminal`);
  url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
  url.searchParams.set("cols", String(cols));
  url.searchParams.set("rows", String(rows));
  const token = localStorage.getItem("auth_token");
  if (token) url.searchParams.set("access_token", token);
  return url.toString();
}

export default function LoopRunTerminal({ loopRunId, title, onClose }: Props) {
  return (
    <TerminalView
      connectionKey={loopRunId}
      buildWsUrl={(cols, rows) => buildWebSocketUrl(loopRunId, cols, rows)}
      title={title}
      errorHint="Connection error. The worktree may have been cleaned up."
      ariaLabel={`Worktree terminal for ${title}`}
      onClose={onClose}
    />
  );
}
