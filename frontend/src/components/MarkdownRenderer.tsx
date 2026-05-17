import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import "./MarkdownRenderer.css";

interface MarkdownRendererProps {
  content: string;
  className?: string;
}

/**
 * Renders markdown content with GFM support (tables, strikethrough, etc.).
 * Falls back to plain text when content is empty.
 */
export default function MarkdownRenderer({ content, className }: MarkdownRendererProps) {
  if (!content) {
    return null;
  }

  return (
    <div className={`markdown-body ${className ?? ""}`}>
      <ReactMarkdown remarkPlugins={[remarkGfm]}>{content}</ReactMarkdown>
    </div>
  );
}
