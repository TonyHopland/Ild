import { useCallback, useEffect, useId, useRef, useState } from "react";
import { useMatch } from "react-router";
import { useChatEnabled } from "../hooks/useChatEnabled";
import {
  clampFabPosition,
  clampPanelSize,
  loadFabPosition,
  loadPanelPosition,
  loadPanelSize,
  loadSidebarOpen,
  panelPosition,
  saveFabPosition,
  savePanelPosition,
  savePanelSize,
  saveSidebarOpen,
  SIDEBAR_SIDE_BY_SIDE_MIN_WIDTH,
  viewportSize,
  type Point,
  type Size,
} from "./chatPlacement";
import { useChatInbox } from "./ChatInbox";
import { UnreadDot } from "./ChatRename";
import ChatWorkspace from "./ChatWorkspace";
import "./ChatBubble.css";

// Treat tiny pointer movements as a click, not a drag, so the icon still opens
// the panel when tapped.
const DRAG_THRESHOLD_PX = 4;

/**
 * Persistent chat bubble (ADR-0010) with retained chat history (ADR-0013).
 * Mounted globally so it survives navigation; chats live server-side. The bubble
 * lists the user's past chats and resumes any of them with its full transcript;
 * chats are retained until explicitly deleted (per-chat or "delete all").
 *
 * The Chat tab shows chat full screen, so the bubble shows nothing there, and its
 * chat is not mounted: the same chat is never on screen twice. Its frame stays, so
 * it comes back as it was left, on the chat the tab had open.
 */
export default function ChatBubble() {
  const onChatTab = useMatch("/chat/*") !== null;
  const [open, setOpen] = useState(false);
  const { loaded, unreadCount, activeChatId } = useChatInbox();

  // Placement: a draggable icon position and a resizable panel size, both
  // persisted and kept inside the viewport.
  const chatEnabled = useChatEnabled();
  const [fabPos, setFabPos] = useState<Point>(loadFabPosition);
  const [panelSize, setPanelSize] = useState<Size>(loadPanelSize);
  // The panel's own position once the user drags its header; until then it stays
  // anchored to the icon (null).
  const [panelOverride, setPanelOverride] = useState<Point | null>(loadPanelPosition);
  // Set while a drag exceeds the threshold so the trailing click does not also
  // open the panel.
  const draggedRef = useRef(false);
  // Latest panel size, so the window-resize handler can re-clamp the panel
  // position without re-subscribing on every size change.
  const panelSizeRef = useRef(panelSize);
  panelSizeRef.current = panelSize;

  // The chat list: beside the chat in a wide panel, in place of it in a narrow one.
  // Stored only when the user opens or closes it, so until then each load picks the
  // default from the panel's width.
  const [sidebarOpen, setSidebarOpenState] = useState(() => loadSidebarOpen(panelSize.width));
  const setSidebarOpen = useCallback((next: boolean) => {
    setSidebarOpenState(next);
    saveSidebarOpen(next);
  }, []);
  const wide = panelSize.width >= SIDEBAR_SIDE_BY_SIDE_MIN_WIDTH;
  const unreadBadgeId = useId();
  const chatCovered = sidebarOpen && !wide;

  // Persist placement changes and re-clamp into view whenever the window resizes.
  useEffect(() => {
    saveFabPosition(fabPos);
  }, [fabPos]);
  useEffect(() => {
    savePanelSize(panelSize);
  }, [panelSize]);
  useEffect(() => {
    if (panelOverride) savePanelPosition(panelOverride);
  }, [panelOverride]);
  useEffect(() => {
    const onResize = () => {
      const vp = viewportSize();
      setFabPos((p) => clampFabPosition(p, vp));
      setPanelSize((s) => clampPanelSize(s, vp));
      setPanelOverride((p) => (p ? panelPosition(p, panelSizeRef.current, vp) : p));
    };
    window.addEventListener("resize", onResize);
    return () => window.removeEventListener("resize", onResize);
  }, []);

  const startDrag = useCallback(
    (e: React.PointerEvent) => {
      draggedRef.current = false;
      const origin = { px: e.clientX, py: e.clientY, ox: fabPos.x, oy: fabPos.y };
      const onMove = (ev: PointerEvent) => {
        const dx = ev.clientX - origin.px;
        const dy = ev.clientY - origin.py;
        if (Math.abs(dx) > DRAG_THRESHOLD_PX || Math.abs(dy) > DRAG_THRESHOLD_PX) {
          draggedRef.current = true;
        }
        setFabPos(clampFabPosition({ x: origin.ox + dx, y: origin.oy + dy }, viewportSize()));
      };
      const onUp = () => {
        window.removeEventListener("pointermove", onMove);
        window.removeEventListener("pointerup", onUp);
      };
      window.addEventListener("pointermove", onMove);
      window.addEventListener("pointerup", onUp);
    },
    [fabPos.x, fabPos.y],
  );

  const startResize = useCallback(
    (e: React.PointerEvent) => {
      e.preventDefault();
      const origin = { px: e.clientX, py: e.clientY, w: panelSize.width, h: panelSize.height };
      const onMove = (ev: PointerEvent) => {
        const vp = viewportSize();
        const next = clampPanelSize(
          {
            width: origin.w + (ev.clientX - origin.px),
            height: origin.h + (ev.clientY - origin.py),
          },
          vp,
        );
        setPanelSize(next);
        // Keep a moved panel on-screen as it grows toward the viewport edge.
        setPanelOverride((p) => (p ? panelPosition(p, next, vp) : p));
      };
      const onUp = () => {
        window.removeEventListener("pointermove", onMove);
        window.removeEventListener("pointerup", onUp);
      };
      window.addEventListener("pointermove", onMove);
      window.addEventListener("pointerup", onUp);
    },
    [panelSize.width, panelSize.height],
  );

  const startHeaderDrag = useCallback(
    (e: React.PointerEvent) => {
      // Let the header's own controls (chat list, close) work without dragging.
      if ((e.target as HTMLElement).closest("button, input")) return;
      const base = panelOverride ?? panelPosition(fabPos, panelSize, viewportSize());
      const origin = { px: e.clientX, py: e.clientY, ox: base.x, oy: base.y };
      const onMove = (ev: PointerEvent) => {
        setPanelOverride(
          panelPosition(
            { x: origin.ox + (ev.clientX - origin.px), y: origin.oy + (ev.clientY - origin.py) },
            panelSize,
            viewportSize(),
          ),
        );
      };
      const onUp = () => {
        window.removeEventListener("pointermove", onMove);
        window.removeEventListener("pointerup", onUp);
      };
      window.addEventListener("pointermove", onMove);
      window.addEventListener("pointerup", onUp);
    },
    [panelOverride, fabPos, panelSize],
  );

  const openPanel = useCallback(() => {
    setOpen(true);
  }, []);

  const onFabClick = useCallback(() => {
    // Swallow the click that ends a drag; a genuine click re-arms and opens.
    if (draggedRef.current) {
      draggedRef.current = false;
      return;
    }
    openPanel();
  }, [openPanel]);

  // A narrow panel's list gives way to the chat it has put on screen.
  const foldNarrowList = useCallback(() => {
    if (panelSizeRef.current.width < SIDEBAR_SIDE_BY_SIDE_MIN_WIDTH) setSidebarOpen(false);
  }, [setSidebarOpen]);

  if (onChatTab) return null;

  return (
    <ChatWorkspace
      initialChatId={activeChatId}
      routeContext
      shown={chatEnabled && open}
      covered={chatCovered}
      sidebarShown={sidebarOpen}
      onChatShown={foldNarrowList}
    >
      {({ title, error, sidebar, content }) => {
        if (!chatEnabled) {
          return null;
        }

        if (!open) {
          return (
            <button
              type="button"
              className="chat-bubble-fab"
              aria-label="Open chat"
              style={{ left: fabPos.x, top: fabPos.y }}
              onPointerDown={startDrag}
              onClick={onFabClick}
            >
              💬{unreadCount > 0 && <UnreadDot />}
            </button>
          );
        }

        const panelPos = panelPosition(panelOverride ?? fabPos, panelSize, viewportSize());
        // The button's name stays "Chat list"; the badge reaches screen readers as its description.
        const showUnreadBadge = !sidebarOpen && unreadCount > 0;

        return (
          <div
            className="chat-panel"
            role="dialog"
            aria-label="AI chat"
            style={{
              left: panelPos.x,
              top: panelPos.y,
              width: panelSize.width,
              height: panelSize.height,
            }}
          >
            <div className="chat-panel-header" onPointerDown={startHeaderDrag}>
              <button
                type="button"
                className="chat-link-btn chat-sidebar-toggle"
                aria-label="Chat list"
                aria-expanded={sidebarOpen}
                aria-describedby={showUnreadBadge ? unreadBadgeId : undefined}
                onClick={() => setSidebarOpen(!sidebarOpen)}
              >
                ☰
                {showUnreadBadge && (
                  <span id={unreadBadgeId} className="chat-sidebar-badge">
                    {unreadCount}
                  </span>
                )}
              </button>
              <div className="chat-panel-heading">
                <span className="chat-panel-title">{title ?? "AI Chat"}</span>
              </div>
              <div className="chat-panel-header-actions">
                <button
                  type="button"
                  className="chat-link-btn"
                  aria-label="Close chat"
                  onClick={() => setOpen(false)}
                >
                  ✕
                </button>
              </div>
            </div>

            {error && <div className="chat-error">{error}</div>}

            {!loaded ? (
              <div className="chat-panel-body chat-muted">Loading…</div>
            ) : (
              <div className="chat-panel-main">
                {sidebar}
                {content}
              </div>
            )}

            <div
              className="chat-resize-handle"
              role="button"
              tabIndex={-1}
              aria-label="Resize chat"
              onPointerDown={startResize}
            />
          </div>
        );
      }}
    </ChatWorkspace>
  );
}
