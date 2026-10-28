/** Which pane a phone shows: the chat list while it is asked for, the open chat otherwise. Wide screens always show the chat. */
export function phonePaneOf(isMobile: boolean, listShown: boolean): "list" | "chat" {
  return isMobile && listShown ? "list" : "chat";
}
