import { useEffect, useId, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import Icon from "./Icon";

export interface MenuItem {
  /** Stable across renders and unique in its menu: the item's key. */
  id: string;
  label: string;
  onSelect: () => void;
  /** Shown in the danger colour; still one click, the action itself confirms if it needs to. */
  danger?: boolean;
  disabled?: boolean;
}

/**
 * A ⋯ button that opens a short list of secondary actions, so a footer or a
 * header keeps one obvious primary button. It follows the menu pattern: opening
 * moves focus to the first item; the arrow keys, Home and End move between the
 * items that can be chosen; Escape closes it and returns focus to the button,
 * and does not also close the dialog it sits in; Tab closes it and moves on; a
 * click outside closes it.
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
  const buttonRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const menuId = useId();

  const choosable = () =>
    [...(menuRef.current?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]') ?? [])].filter(
      (item) => !item.disabled,
    );

  useEffect(() => {
    if (!open) return;
    choosable()[0]?.focus();
    const onDown = (e: MouseEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", onDown);
    return () => document.removeEventListener("mousedown", onDown);
  }, [open]);

  if (items.length === 0) return null;

  const close = (refocus: boolean) => {
    setOpen(false);
    if (refocus) buttonRef.current?.focus();
  };

  const onMenuKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const all = choosable();
    const at = all.indexOf(document.activeElement as HTMLButtonElement);
    const move = (to: number) => {
      e.preventDefault();
      all[(to + all.length) % all.length]?.focus();
    };
    if (e.key === "ArrowDown") move(at + 1);
    else if (e.key === "ArrowUp") move(at < 0 ? all.length - 1 : at - 1);
    else if (e.key === "Home") move(0);
    else if (e.key === "End") move(all.length - 1);
    else if (e.key === "Tab") close(false);
  };

  return (
    <div
      className="menu-button"
      ref={rootRef}
      onKeyDown={(e) => {
        if (e.key === "Escape" && open) {
          e.stopPropagation();
          close(true);
        }
      }}
    >
      <button
        ref={buttonRef}
        type="button"
        className={`btn btn-secondary ${children ? "" : "btn-icon"} ${small ? "btn-sm" : ""}`}
        aria-label={children ? undefined : label}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? menuId : undefined}
        onClick={() => setOpen((o) => !o)}
        onKeyDown={(e) => {
          // Down or Up on the closed button opens the menu, as Enter and Space do.
          if (!open && (e.key === "ArrowDown" || e.key === "ArrowUp")) {
            e.preventDefault();
            setOpen(true);
          }
        }}
      >
        {children ?? <Icon name="dots" />}
      </button>
      {open && (
        <div
          ref={menuRef}
          className={`menu-popover menu-popover-${placement}`}
          role="menu"
          id={menuId}
          aria-label={label}
          onKeyDown={onMenuKeyDown}
        >
          {items.map((item) => (
            <button
              key={item.id}
              type="button"
              role="menuitem"
              tabIndex={-1}
              className={`menu-item ${item.danger ? "menu-item-danger" : ""}`}
              disabled={item.disabled}
              onClick={() => {
                close(true);
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
