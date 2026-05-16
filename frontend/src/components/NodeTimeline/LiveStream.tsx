import { useRef, useEffect, useCallback, useState } from "react";

interface LiveStreamProps {
  text: string;
}

export default function LiveStream({ text }: LiveStreamProps) {
  const containerRef = useRef<HTMLPreElement | null>(null);
  const isAtBottomRef = useRef(true);
  const [unreadCount, setUnreadCount] = useState(0);

  const handleScroll = useCallback(() => {
    const el = containerRef.current;
    if (!el) return;
    isAtBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 30;
    if (isAtBottomRef.current) {
      setUnreadCount(0);
    }
  }, []);

  useEffect(() => {
    if (!text) return;
    const el = containerRef.current;
    if (!el) return;
    if (isAtBottomRef.current) {
      el.scrollTop = el.scrollHeight;
    } else {
      setUnreadCount((prev) => prev + 1);
    }
  }, [text]);

  if (!text) {
    return (
      <div className="node-detail-section node-livestream-section">
        <h4>Live Output</h4>
        <pre className="livestream-container livestream-empty">Waiting for output...</pre>
      </div>
    );
  }

  return (
    <div className="node-detail-section node-livestream-section">
      <h4>Live Output</h4>
      <pre ref={containerRef} className="livestream-container" onScroll={handleScroll}>
        {text}
      </pre>
      {unreadCount > 0 && (
        <button
          className="livestream-scroll-btn"
          onClick={() => {
            containerRef.current?.scrollTo({
              top: containerRef.current.scrollHeight,
              behavior: "smooth",
            });
            setUnreadCount(0);
          }}
        >
          ▼ {unreadCount} new line{unreadCount > 1 ? "s" : ""}
        </button>
      )}
    </div>
  );
}
