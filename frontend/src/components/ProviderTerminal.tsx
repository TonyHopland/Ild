import { useEffect, useRef, useState } from "react";
import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import "@xterm/xterm/css/xterm.css";

const API_BASE: string = (import.meta.env?.VITE_API_BASE as string | undefined) ?? "/api/v1";

interface Props {
  providerId: string;
  providerName: string;
  onClose: () => void;
}

function buildWebSocketUrl(providerId: string, cols: number, rows: number): string {
  const apiBase = API_BASE.startsWith("http") ? API_BASE : `${window.location.origin}${API_BASE}`;
  const url = new URL(`${apiBase}/aiproviders/${providerId}/interactive`);
  url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
  url.searchParams.set("cols", String(cols));
  url.searchParams.set("rows", String(rows));
  const token = localStorage.getItem("auth_token");
  if (token) url.searchParams.set("access_token", token);
  return url.toString();
}

export default function ProviderTerminal({ providerId, providerName, onClose }: Props) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const termRef = useRef<Terminal | null>(null);
  const wsRef = useRef<WebSocket | null>(null);
  const fitRef = useRef<FitAddon | null>(null);
  const [status, setStatus] = useState<"connecting" | "open" | "closed" | "error">("connecting");
  const [errorText, setErrorText] = useState<string>("");

  useEffect(() => {
    if (!containerRef.current) return;

    const term = new Terminal({
      cursorBlink: true,
      fontFamily: "ui-monospace, SFMono-Regular, Menlo, Consolas, monospace",
      fontSize: 13,
      theme: { background: "#0f0f1f" },
      convertEol: false,
      scrollback: 5000,
    });
    const fit = new FitAddon();
    term.loadAddon(fit);
    term.open(containerRef.current);
    fit.fit();

    termRef.current = term;
    fitRef.current = fit;

    const ws = new WebSocket(buildWebSocketUrl(providerId, term.cols, term.rows));
    ws.binaryType = "arraybuffer";
    wsRef.current = ws;

    ws.onopen = () => {
      setStatus("open");
      ws.send(JSON.stringify({ type: "resize", cols: term.cols, rows: term.rows }));
    };
    ws.onmessage = (ev) => {
      if (typeof ev.data === "string") {
        term.write(ev.data);
      } else {
        term.write(new Uint8Array(ev.data as ArrayBuffer));
      }
    };
    ws.onerror = () => {
      setStatus("error");
      setErrorText("Connection error. Check that the provider binary is installed and on PATH.");
    };
    ws.onclose = (ev) => {
      setStatus("closed");
      if (ev.code !== 1000 && !errorText) {
        setErrorText(ev.reason || `Connection closed (${ev.code}).`);
      }
    };

    const dataDisposable = term.onData((data) => {
      if (ws.readyState === WebSocket.OPEN) {
        ws.send(new TextEncoder().encode(data));
      }
    });

    let resizeTimer: ReturnType<typeof setTimeout> | null = null;
    const handleResize = () => {
      if (resizeTimer) clearTimeout(resizeTimer);
      resizeTimer = setTimeout(() => {
        try {
          fit.fit();
          if (ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify({ type: "resize", cols: term.cols, rows: term.rows }));
          }
        } catch {
          /* ignore fit errors when panel is unmounted */
        }
      }, 100);
    };
    window.addEventListener("resize", handleResize);
    const ro = new ResizeObserver(handleResize);
    ro.observe(containerRef.current);

    term.focus();

    return () => {
      window.removeEventListener("resize", handleResize);
      ro.disconnect();
      dataDisposable.dispose();
      try {
        ws.close(1000, "client closed");
      } catch {
        /* noop */
      }
      try {
        term.dispose();
      } catch {
        /* noop */
      }
      termRef.current = null;
      wsRef.current = null;
      fitRef.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [providerId]);

  return (
    <div className="modal-overlay" onMouseDown={onClose}>
      <div
        className="pt-modal"
        onMouseDown={(e) => e.stopPropagation()}
        role="dialog"
        aria-label={`Interactive terminal for ${providerName}`}
      >
        <div className="pt-header">
          <div className="pt-title">
            <span className="pt-name">{providerName}</span>
            <span className={`pt-status pt-status-${status}`}>
              {status === "connecting"
                ? "Connecting…"
                : status === "open"
                  ? "Connected"
                  : status === "closed"
                    ? "Closed"
                    : "Error"}
            </span>
          </div>
          <button className="btn btn-secondary btn-small" onClick={onClose}>
            Close
          </button>
        </div>
        <div className="pt-body" ref={containerRef} />
        {errorText && <div className="pt-error">{errorText}</div>}
      </div>
      <style>{`
        .pt-modal {
          background-color: #0f0f1f;
          border-radius: 0.5rem;
          border: 1px solid #2d2d44;
          width: 90vw;
          height: 80vh;
          display: flex;
          flex-direction: column;
          overflow: hidden;
        }
        .pt-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          padding: 0.5rem 0.75rem;
          border-bottom: 1px solid #2d2d44;
          background-color: #1e1e30;
        }
        .pt-title {
          display: flex;
          align-items: center;
          gap: 0.5rem;
          color: #e0e0e0;
          font-size: 0.85rem;
        }
        .pt-name { font-weight: 600; }
        .pt-status {
          font-size: 0.7rem;
          padding: 0.125rem 0.375rem;
          border-radius: 0.25rem;
          background-color: #2d2d44;
          color: #a0a0b0;
        }
        .pt-status-open { background-color: #1a3a2a; color: #22c55e; }
        .pt-status-error, .pt-status-closed { background-color: #3a1a1a; color: #ef4444; }
        .pt-body {
          flex: 1;
          padding: 0.5rem;
          overflow: hidden;
        }
        .pt-body .xterm,
        .pt-body .xterm-viewport,
        .pt-body .xterm-screen {
          width: 100% !important;
          height: 100% !important;
        }
        .pt-error {
          padding: 0.5rem 0.75rem;
          background-color: #3a1a1a;
          color: #fca5a5;
          font-size: 0.75rem;
          border-top: 1px solid #5b1f1f;
        }
      `}</style>
    </div>
  );
}
