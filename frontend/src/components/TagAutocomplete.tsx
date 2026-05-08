import { useState, useRef, useCallback, useEffect } from "react";
import "./PromptEditor.css";

interface TagAutocompleteProps {
  value: string;
  onChange: (value: string) => void;
  /** Suggestion source — typically loop template names. */
  options: string[];
  id?: string;
  placeholder?: string;
}

/**
 * Comma-separated tag input with per-segment autocomplete. Suggestions
 * filter by the partial after the last comma in the input value, mirroring
 * the prompt-editor placeholder picker. Click or Enter inserts the
 * selected suggestion in place of the partial and appends ", " so the user
 * can keep typing the next tag.
 */
export default function TagAutocomplete({
  value,
  onChange,
  options,
  id,
  placeholder,
}: TagAutocompleteProps) {
  const [showSuggestions, setShowSuggestions] = useState(false);
  const [filtered, setFiltered] = useState<string[]>([]);
  const [selectedIndex, setSelectedIndex] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLUListElement>(null);

  // The "current segment" is everything after the last comma up to the
  // cursor. We replace just that on selection so earlier tags are kept.
  const getSegment = useCallback((text: string, cursor: number) => {
    const before = text.slice(0, cursor);
    const lastComma = before.lastIndexOf(",");
    const segStart = lastComma === -1 ? 0 : lastComma + 1;
    const partial = before.slice(segStart).trimStart();
    const partialStart =
      segStart + (before.slice(segStart).length - before.slice(segStart).trimStart().length);
    return { partial, partialStart, partialEnd: cursor };
  }, []);

  const updateSuggestions = useCallback(
    (text: string, cursor: number) => {
      const { partial } = getSegment(text, cursor);
      const lower = partial.toLowerCase();
      const matches = lower
        ? options.filter((o) => o.toLowerCase().includes(lower) && o.toLowerCase() !== lower)
        : options;
      if (matches.length > 0) {
        setFiltered(matches);
        setSelectedIndex(0);
        setShowSuggestions(true);
      } else {
        setShowSuggestions(false);
        setFiltered([]);
      }
    },
    [getSegment, options],
  );

  const handleChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const text = e.target.value;
    onChange(text);
    const cursor = e.target.selectionStart ?? text.length;
    updateSuggestions(text, cursor);
  };

  const insert = useCallback(
    (suggestion: string) => {
      const ta = inputRef.current;
      if (!ta) return;
      const cursor = ta.selectionStart ?? value.length;
      const { partialStart, partialEnd } = getSegment(value, cursor);
      const before = value.slice(0, partialStart);
      const after = value.slice(partialEnd);
      // Append ", " unless the user is already followed by one — keeps the
      // cursor positioned for the next tag.
      const trailing = after.trimStart().length > 0 ? "" : ", ";
      const newVal = `${before}${suggestion}${trailing}${after}`;
      onChange(newVal);
      setShowSuggestions(false);
      setFiltered([]);
      requestAnimationFrame(() => {
        const newCursor = (before + suggestion + trailing).length;
        ta.focus();
        ta.setSelectionRange(newCursor, newCursor);
      });
    },
    [value, onChange, getSegment],
  );

  const handleKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (!showSuggestions || filtered.length === 0) return;
    if (e.key === "ArrowDown") {
      e.preventDefault();
      setSelectedIndex((p) => (p + 1) % filtered.length);
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setSelectedIndex((p) => (p - 1 + filtered.length) % filtered.length);
    } else if (e.key === "Enter" || e.key === "Tab") {
      e.preventDefault();
      insert(filtered[selectedIndex]);
    } else if (e.key === "Escape") {
      setShowSuggestions(false);
      setFiltered([]);
    }
  };

  const handleBlur = () => {
    // Delay so a click on a suggestion item is registered first.
    setTimeout(() => {
      setShowSuggestions(false);
      setFiltered([]);
    }, 150);
  };

  const handleFocus = (e: React.FocusEvent<HTMLInputElement>) => {
    const cursor = e.target.selectionStart ?? value.length;
    updateSuggestions(value, cursor);
  };

  useEffect(() => {
    if (showSuggestions && listRef.current) {
      const li = listRef.current.children[selectedIndex] as HTMLElement | undefined;
      li?.scrollIntoView?.({ block: "nearest" });
    }
  }, [selectedIndex, showSuggestions]);

  return (
    <div className="prompt-editor-wrapper">
      <input
        ref={inputRef}
        id={id}
        type="text"
        className="prompt-editor-textarea"
        value={value}
        placeholder={placeholder}
        onChange={handleChange}
        onKeyDown={handleKeyDown}
        onBlur={handleBlur}
        onFocus={handleFocus}
      />
      {showSuggestions && filtered.length > 0 && (
        <ul ref={listRef} className="prompt-suggestions-list">
          {filtered.map((s, i) => (
            <li
              key={s}
              className={`prompt-suggestion-item ${i === selectedIndex ? "selected" : ""}`}
              onMouseDown={(e) => {
                e.preventDefault();
                insert(s);
              }}
            >
              <span className="prompt-suggestion-key">{s}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
