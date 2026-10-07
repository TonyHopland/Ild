import { useRef, useState } from "react";

/** Marks the chat button, or a past chat, as holding a reply the user has not read. */
export function UnreadDot() {
  return <span className="chat-unread-dot" role="img" aria-label="New messages" />;
}

/** Renames a chat; false when another rename of it is still on its way. */
export type RenameChat = (chatSessionId: string, name: string) => Promise<boolean>;

/** The name the server stores for a rename: without NUL characters, trimmed. */
const storedName = (draft: string) => draft.replace(/\0/g, "").trim();

/** One edit of a chat's title: its draft, save and error go with it when it closes. */
export function RenameForm({
  chatSessionId,
  initial,
  renameChat,
  blocked,
  onClose,
}: {
  chatSessionId: string;
  initial: string;
  renameChat: RenameChat;
  blocked: boolean;
  onClose: () => void;
}) {
  const [draft, setDraft] = useState(initial);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const empty = storedName(draft) === "";

  const save = async () => {
    const name = storedName(draft);
    if (!name || saving || blocked) return;
    setSaving(true);
    setError(null);
    try {
      if (await renameChat(chatSessionId, name)) onClose();
      else setSaving(false);
    } catch (e) {
      setSaving(false);
      setError((e as { message?: string })?.message ?? "Could not rename chat.");
    }
  };

  return (
    <form
      className="chat-rename"
      onSubmit={(e) => {
        e.preventDefault();
        void save();
      }}
    >
      <input
        className="chat-rename-input"
        aria-label="Chat name"
        maxLength={120}
        autoFocus
        // An edit made mid-save would be closed away unsaved when the save returns.
        readOnly={saving}
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => {
          if (e.key !== "Escape") return;
          // A page under the chat, such as an open work item, closes on Escape too.
          e.stopPropagation();
          onClose();
        }}
      />
      <button type="submit" className="chat-link-btn" disabled={empty || saving || blocked}>
        Save
      </button>
      <button type="button" className="chat-link-btn" onClick={onClose}>
        Cancel
      </button>
      {error && (
        <span className="chat-rename-error" role="alert">
          {error}
        </span>
      )}
    </form>
  );
}

/**
 * Which edit of a title is open, if any. Each edit is a fresh form keyed by its
 * generation, and only that edit can close itself.
 */
export function useTitleEdit() {
  const [editing, setEditing] = useState<number | null>(null);
  const generation = useRef(0);
  return {
    editing,
    begin: () => setEditing(++generation.current),
    closer: (edit: number) => () => setEditing((current) => (current === edit ? null : current)),
  };
}
