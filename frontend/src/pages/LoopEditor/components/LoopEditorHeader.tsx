interface LoopEditorHeaderProps {
  isNarrow: boolean;
  sidebarVisible: boolean;
  isNewTemplate: boolean;
  newTemplateName: string;
  saveSuccess: boolean;
  canSave: boolean;
  isSaving: boolean;
  readOnlyVersion: number | null;
  onToggleSidebar: () => void;
  onExitReadOnlyMode: () => void;
  onNewTemplateNameChange: (value: string) => void;
  onSave: () => void;
  onCreateTemplate: () => void;
}

export function LoopEditorHeader({
  isNarrow,
  sidebarVisible,
  isNewTemplate,
  newTemplateName,
  saveSuccess,
  canSave,
  isSaving,
  readOnlyVersion,
  onToggleSidebar,
  onExitReadOnlyMode,
  onNewTemplateNameChange,
  onSave,
  onCreateTemplate,
}: LoopEditorHeaderProps) {
  return (
    <div className="loop-editor-header">
      <h1 className="page-title">Loop Editor</h1>
      {readOnlyVersion !== null && (
        <div className="readonly-banner" onClick={onExitReadOnlyMode}>
          Viewing v{readOnlyVersion} (read-only) - click to exit
        </div>
      )}
      <div className="header-actions">
        {!isNarrow && (
          <button
            className="sidebar-toggle-btn"
            onClick={onToggleSidebar}
            aria-label="Toggle sidebar"
          >
            {sidebarVisible ? "◀" : "▶"}
          </button>
        )}
        {isNewTemplate && (
          <input
            type="text"
            className="new-template-name-input"
            placeholder="Template name"
            value={newTemplateName}
            onChange={(event) => onNewTemplateNameChange(event.target.value)}
          />
        )}
        {saveSuccess && <span className="save-success">Saved!</span>}
        {canSave && readOnlyVersion === null && (
          <button className="save-btn" onClick={onSave} disabled={isSaving}>
            {isSaving ? "Saving..." : "Save"}
          </button>
        )}
        <button className="new-template-btn" onClick={onCreateTemplate}>
          New Template
        </button>
      </div>
    </div>
  );
}
