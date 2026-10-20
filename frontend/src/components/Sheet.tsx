import { useEffect, useRef, type ReactNode } from "react";
import "./Sheet.css";

/**
 * A panel that slides up from the bottom of a phone screen over a dimmed page:
 * the app menu, the board's filters. Tapping the page behind it or pressing
 * Escape closes it, and focus goes back to whatever opened it.
 */
export default function Sheet({
  title,
  onClose,
  children,
}: {
  title: string;
  onClose: () => void;
  children: ReactNode;
}) {
  const panelRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null;
    panelRef.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => {
      window.removeEventListener("keydown", onKey);
      opener?.focus?.();
    };
  }, [onClose]);

  return (
    <>
      <div className="sheet-scrim" onClick={onClose} />
      <div
        ref={panelRef}
        className="sheet"
        role="dialog"
        aria-modal="true"
        aria-label={title}
        tabIndex={-1}
      >
        <div className="sheet-grab" aria-hidden="true" />
        <h2 className="sheet-title">{title}</h2>
        {children}
      </div>
    </>
  );
}
