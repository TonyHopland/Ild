import { useRef, useEffect } from "react";
import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import "@xterm/xterm/css/xterm.css";

interface LiveStreamProps {
  /**
   * The full output captured so far (backlog + live, already concatenated).
   * Treated as append-only: each render writes only the delta past what the
   * terminal has already shown, so the same bytes are never written twice.
   */
  text: string;
}

/**
 * Read-only terminal view of a run's live output. Reuses the post-run xterm
 * setup (ANSI colours, cursor handling, scrollback) but drops the keystroke
 * input pump — output is fed from the `text` prop, not a socket.
 */
export default function LiveStream({ text }: LiveStreamProps) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const termRef = useRef<Terminal | null>(null);
  const fitRef = useRef<FitAddon | null>(null);
  // How many characters of `text` the terminal has already rendered.
  const writtenRef = useRef(0);
  const hasText = text.length > 0;

  // Mount the terminal lazily once there is output to show. Keeping it out of
  // the DOM while empty avoids spinning up xterm for runs that never stream.
  useEffect(() => {
    if (!hasText || !containerRef.current || termRef.current) return;

    const term = new Terminal({
      cursorBlink: false,
      disableStdin: true,
      fontFamily: "ui-monospace, SFMono-Regular, Menlo, Consolas, monospace",
      fontSize: 13,
      theme: { background: "#030711" },
      convertEol: true,
      scrollback: 10000,
    });
    const fit = new FitAddon();
    term.loadAddon(fit);
    term.open(containerRef.current);
    try {
      fit.fit();
    } catch {
      /* dimensions unavailable (e.g. detached/hidden) — render unfitted */
    }

    termRef.current = term;
    fitRef.current = fit;
    writtenRef.current = 0;

    let resizeTimer: ReturnType<typeof setTimeout> | null = null;
    const handleResize = () => {
      if (resizeTimer) clearTimeout(resizeTimer);
      resizeTimer = setTimeout(() => {
        try {
          fit.fit();
        } catch {
          /* ignore fit errors when the panel is unmounted/hidden */
        }
      }, 100);
    };
    window.addEventListener("resize", handleResize);
    const ro = typeof ResizeObserver !== "undefined" ? new ResizeObserver(handleResize) : null;
    ro?.observe(containerRef.current);

    return () => {
      window.removeEventListener("resize", handleResize);
      ro?.disconnect();
      if (resizeTimer) clearTimeout(resizeTimer);
      try {
        term.dispose();
      } catch {
        /* noop */
      }
      termRef.current = null;
      fitRef.current = null;
      writtenRef.current = 0;
    };
  }, [hasText]);

  // Stream the delta into the terminal. `text` is append-only across a run;
  // if it ever shrinks (a new run reset it) we clear and start over.
  useEffect(() => {
    const term = termRef.current;
    if (!term) return;
    if (text.length < writtenRef.current) {
      term.reset();
      writtenRef.current = 0;
    }
    if (text.length > writtenRef.current) {
      term.write(text.slice(writtenRef.current));
      writtenRef.current = text.length;
    }
  }, [text]);

  return (
    <div className="node-detail-section node-livestream-section">
      <h4>Live Output</h4>
      <div
        ref={containerRef}
        className={`livestream-container${hasText ? "" : " livestream-empty"}`}
      >
        {!hasText && "Waiting for output..."}
      </div>
    </div>
  );
}
