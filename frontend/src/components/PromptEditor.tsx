import { useState, useRef, useCallback, useEffect } from "react";
import "./PromptEditor.css";

const PLACEHOLDERS = [
  { key: "WorkItem.Title", label: "WorkItem.Title", desc: "Title of the work item" },
  {
    key: "WorkItem.Description",
    label: "WorkItem.Description",
    desc: "Description of the work item",
  },
  { key: "EventLog.Summary", label: "EventLog.Summary", desc: "All event log entries" },
  { key: "EventLog.LastN", label: "EventLog.LastN", desc: "Last 10 event log entries" },
  { key: "Node.Input", label: "Node.Input", desc: "Output from the previous node" },
  {
    key: "PreviousNode.Output",
    label: "PreviousNode.Output",
    desc: "Output from the previous node",
  },
  { key: "WorkTree.Diff", label: "WorkTree.Diff", desc: "Git diff of the worktree" },
  {
    key: "WorkTree.File:",
    label: "WorkTree.File:<path>",
    desc: "Contents of a file in the worktree",
  },
];

interface PromptEditorProps {
  value: string;
  onChange: (value: string) => void;
  rows?: number;
  id?: string;
}

export default function PromptEditor({ value, onChange, rows = 3, id }: PromptEditorProps) {
  const [showSuggestions, setShowSuggestions] = useState(false);
  const [filteredItems, setFilteredItems] = useState<typeof PLACEHOLDERS>([]);
  const [selectedIndex, setSelectedIndex] = useState(0);
  const [triggerPos, setTriggerPos] = useState<{ start: number; end: number } | null>(null);
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const listRef = useRef<HTMLUListElement>(null);

  const checkTrigger = useCallback((text: string, cursorPos: number) => {
    const beforeCursor = text.slice(0, cursorPos);
    const match = beforeCursor.match(/{{\s*([A-Za-z][A-Za-z0-9_.:/\\-]*)?$/);
    if (match) {
      const openBraceIdx = match.index!;
      const start = openBraceIdx;
      const end = cursorPos;
      const partial = (match[1] ?? "").trim();

      const filtered = partial
        ? PLACEHOLDERS.filter((p) => p.key.toLowerCase().startsWith(partial.toLowerCase()))
        : PLACEHOLDERS;

      if (filtered.length > 0) {
        setTriggerPos({ start, end });
        setFilteredItems(filtered);
        setSelectedIndex(0);
        setShowSuggestions(true);
        return;
      }
    }
    setShowSuggestions(false);
    setFilteredItems([]);
    setTriggerPos(null);
  }, []);

  const handleChange = (e: React.ChangeEvent<HTMLTextAreaElement>) => {
    const newText = e.target.value;
    onChange(newText);
    const cursorPos = e.target.selectionStart ?? newText.length;
    checkTrigger(newText, cursorPos);
  };

  const insertPlaceholder = useCallback(
    (key: string) => {
      if (!triggerPos || !textareaRef.current) return;

      const ta = textareaRef.current;
      const before = value.slice(0, triggerPos.start);
      const after = value.slice(triggerPos.end);
      const newVal = `${before}{{${key}}}${after}`;

      onChange(newVal);
      setShowSuggestions(false);
      setFilteredItems([]);
      setTriggerPos(null);

      requestAnimationFrame(() => {
        const newCursorPos = triggerPos.start + `{{${key}}}`.length;
        ta.focus();
        ta.setSelectionRange(newCursorPos, newCursorPos);
      });
    },
    [triggerPos, value, onChange],
  );

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (!showSuggestions || filteredItems.length === 0) return;

    if (e.key === "ArrowDown") {
      e.preventDefault();
      setSelectedIndex((prev) => (prev + 1) % filteredItems.length);
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setSelectedIndex((prev) => (prev - 1 + filteredItems.length) % filteredItems.length);
    } else if (e.key === "Enter" || e.key === "Tab") {
      e.preventDefault();
      insertPlaceholder(filteredItems[selectedIndex].key);
    } else if (e.key === "Escape") {
      setShowSuggestions(false);
      setFilteredItems([]);
      setTriggerPos(null);
    }
  };

  const handleBlur = () => {
    setTimeout(() => {
      setShowSuggestions(false);
      setFilteredItems([]);
      setTriggerPos(null);
    }, 150);
  };

  useEffect(() => {
    if (showSuggestions && listRef.current) {
      const li = listRef.current.children[selectedIndex] as HTMLElement;
      li?.scrollIntoView?.({ block: "nearest" });
    }
  }, [selectedIndex, showSuggestions]);

  return (
    <div className="prompt-editor-wrapper">
      <textarea
        ref={textareaRef}
        id={id}
        className="prompt-editor-textarea"
        rows={rows}
        value={value}
        onChange={handleChange}
        onKeyDown={handleKeyDown}
        onBlur={handleBlur}
      />
      {showSuggestions && filteredItems.length > 0 && (
        <ul ref={listRef} className="prompt-suggestions-list">
          {filteredItems.map((item, i) => (
            <li
              key={item.key}
              className={`prompt-suggestion-item ${i === selectedIndex ? "selected" : ""}`}
              onMouseDown={(e) => {
                e.preventDefault();
                insertPlaceholder(item.key);
              }}
            >
              <span className="prompt-suggestion-key">{`{{${item.key}}}`}</span>
              <span className="prompt-suggestion-desc">{item.desc}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
