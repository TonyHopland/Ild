interface LoopEditorHeaderProps {
  readOnlyVersion: number | null;
  onExitReadOnlyMode: () => void;
}

export function LoopEditorHeader({ readOnlyVersion, onExitReadOnlyMode }: LoopEditorHeaderProps) {
  return (
    <div className="loop-editor-header">
      <h1 className="page-title">Loop Editor</h1>
      {readOnlyVersion !== null && (
        <div className="readonly-banner" onClick={onExitReadOnlyMode}>
          Viewing v{readOnlyVersion} (read-only) - click to exit
        </div>
      )}
    </div>
  );
}
