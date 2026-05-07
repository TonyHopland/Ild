interface NodeOutputSectionProps {
  output: string | null;
  error: string | null;
}

export default function NodeOutputSection({ output, error }: NodeOutputSectionProps) {
  const content = error ?? output ?? "";

  if (!content) {
    return (
      <div className="node-detail-section node-output-section">
        <h4>Output</h4>
        <pre className="node-output-content">No output</pre>
      </div>
    );
  }

  return (
    <div className="node-detail-section node-output-section">
      <h4>Output</h4>
      <pre className="node-output-content">{content}</pre>
    </div>
  );
}
