import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import Icon from "./Icon";

export interface MenuItem {
  label: string;
  onSelect: () => void;
  /** Shown in the danger colour; still one click, the action itself confirms if it needs to. */
  danger?: boolean;
  disabled?: boolean;
}

/**
 * A ⋯ button that opens a short list of secondary actions, so a footer or a
 * header keeps one obvious primary button. Escape or a click outside closes it;
 * Escape does not also close the dialog it sits in.
 */
export default function MenuButton({
  label,
  items,
  placement = "up",
  small = false,
  children,
}: {
  /** Accessible name of the button, e.g. "More actions". */
  label: string;
  items: MenuItem[];
  /** Open above the button (a footer) or below it (a header). */
  placement?: "up" | "down";
  small?: boolean;
  /** Replaces the ⋯ icon as the button's content. */
  children?: ReactNode;
}) {
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const menuId = useId();

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", onDown);
    return () => document.removeEventListener("mousedown", onDown);
  }, [open]);

  if (items.length === 0) return null;

  return (
    <div
      className="menu-button"
      ref={rootRef}
      onKeyDown={(e) => {
        if (e.key === "Escape" && open) {
          e.stopPropagation();
          setOpen(false);
        }
      }}
    >
      <button
        type="button"
        className={`btn btn-secondary ${children ? "" : "btn-icon"} ${small ? "btn-sm" : ""}`}
        aria-label={children ? undefined : label}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? menuId : undefined}
        onClick={() => setOpen((o) => !o)}
      >
        {children ?? <Icon name="dots" />}
      </button>
      {open && (
        <div className={`menu-popover menu-popover-${placement}`} role="menu" id={menuId}>
          {items.map((item) => (
            <button
              key={item.label}
              type="button"
              role="menuitem"
              className={`menu-item ${item.danger ? "menu-item-danger" : ""}`}
              disabled={item.disabled}
              onClick={() => {
                setOpen(false);
                item.onSelect();
              }}
            >
              {item.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
