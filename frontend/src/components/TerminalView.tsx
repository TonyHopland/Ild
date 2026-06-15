import { useEffect, useRef, useState } from "react";
import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import "@xterm/xterm/css/xterm.css";
import { createClipboardKeyHandler } from "../utils/terminalClipboard";

interface Props {
  /** Stable identity for this connection. Changing it tears down the socket and reconnects. */
  connectionKey: string;
  /** Builds the WebSocket URL given the initial terminal dimensions. */
  buildWsUrl: (cols: number, rows: number) => string;
  /** Title shown in the modal header. */
  title: string;
  /** Optional explanatory message shown when the socket errors out. */
  errorHint?: string;
  /** Optional aria-label override. Defaults to "Interactive terminal for {title}". */
  ariaLabel?: string;
  /**
   * Render inline (filling the parent) instead of as a centered modal overlay.
   * Used when the terminal lives inside a tab panel rather than on top of one.
   */
  embedded?: boolean;
  onClose: () => void;
}

export default function TerminalView({
  connectionKey,
  buildWsUrl,
  title,
  errorHint,
  ariaLabel,
  embedded = false,
  onClose,
}: Props) {
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
      convertEol: true,
      scrollback: 5000,
    });
    const fit = new FitAddon();
    term.loadAddon(fit);
    term.open(containerRef.current);
    fit.fit();

    // Map Ctrl/Cmd+C (copy selection) and Ctrl/Cmd+V (paste) onto the system
    // clipboard so the usual keyboard shortcuts work alongside the right-click
    // menu. Ctrl+C without a selection still falls through to the PTY as SIGINT.
    term.attachCustomKeyEventHandler(createClipboardKeyHandler(term));

    termRef.current = term;
    fitRef.current = fit;

    const ws = new WebSocket(buildWsUrl(term.cols, term.rows));
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
      setErrorText(errorHint ?? "Connection error.");
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
    // Only restart the session when the connection identity changes. The
    // builder/hint props are read once at mount; updating them mid-session
    // wouldn't have a defined meaning.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [connectionKey]);

  const inner = (
    <>
      <div className="pt-header">
        <div className="pt-title">
          <span className="pt-name">{title}</span>
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
    </>
  );

  const styles = (
    <style>{`
        .pt-embedded {
          width: 100%;
          height: 100%;
          display: flex;
          flex-direction: column;
          overflow: hidden;
        }
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
  );

  if (embedded) {
    return (
      <div
        className="pt-embedded"
        role="group"
        aria-label={ariaLabel ?? `Interactive terminal for ${title}`}
      >
        {inner}
        {styles}
      </div>
    );
  }

  return (
    <div className="modal-overlay" onMouseDown={onClose}>
      <div
        className="pt-modal"
        onMouseDown={(e) => e.stopPropagation()}
        role="dialog"
        aria-label={ariaLabel ?? `Interactive terminal for ${title}`}
      >
        {inner}
      </div>
      {styles}
    </div>
  );
}
