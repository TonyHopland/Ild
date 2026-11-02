import ReactMarkdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";
import { opensInNewTab } from "../utils/linkTarget";
import "./MarkdownRenderer.css";

const components: Components = {
  a: ({ node: _node, ...props }) =>
    opensInNewTab(props.href, window.location.href) ? (
      <a {...props} target="_blank" rel="noopener noreferrer" />
    ) : (
      <a {...props} />
    ),
};

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
      <ReactMarkdown remarkPlugins={[remarkGfm]} components={components}>
        {content}
      </ReactMarkdown>
    </div>
  );
}
