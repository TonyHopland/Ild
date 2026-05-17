import type { LoopTemplate } from "../../../types";
import type { LoopTemplateVersion } from "../types";

interface LoopEditorSidebarProps {
  isNarrow: boolean;
  saveSuccess: boolean;
  canSave: boolean;
  isSaving: boolean;
  isNewTemplate: boolean;
  newTemplateName: string;
  readOnlyVersion: number | null;
  showArchived: boolean;
  templates: LoopTemplate[];
  selectedTemplateId: string | null;
  cloningTemplateId: string | null;
  cloneName: string;
  showVersionHistory: boolean;
  versionHistory: LoopTemplateVersion[];
  onToggleSidebar: () => void;
  onNewTemplateNameChange: (value: string) => void;
  onSave: () => void;
  onExport: () => void;
  onCreateTemplate: () => void;
  onImport: () => void;
  onShowArchivedChange: (value: boolean) => void;
  onSelectTemplate: (template: LoopTemplate) => void;
  onStartClone: (template: LoopTemplate) => void;
  onCloneNameChange: (value: string) => void;
  onConfirmClone: (template: LoopTemplate) => void;
  onToggleArchive: (template: LoopTemplate) => void;
  onShowVersionHistory: (template: LoopTemplate) => void;
  onBackToTemplates: () => void;
  onSelectVersion: (loopTemplateId: string, versionNumber: number) => void;
}

export function LoopEditorSidebar({
  isNarrow,
  saveSuccess,
  canSave,
  isSaving,
  isNewTemplate,
  newTemplateName,
  readOnlyVersion,
  showArchived,
  templates,
  selectedTemplateId,
  cloningTemplateId,
  cloneName,
  showVersionHistory,
  versionHistory,
  onToggleSidebar,
  onNewTemplateNameChange,
  onSave,
  onExport,
  onCreateTemplate,
  onImport,
  onShowArchivedChange,
  onSelectTemplate,
  onStartClone,
  onCloneNameChange,
  onConfirmClone,
  onToggleArchive,
  onShowVersionHistory,
  onBackToTemplates,
  onSelectVersion,
}: LoopEditorSidebarProps) {
  const visibleTemplates = templates.filter((template) => showArchived || !template.isArchived);

  return (
    <aside className="loop-editor-sidebar">
      <div className="sidebar-header">
        <div>
          <div className="sidebar-title">Loops</div>
          <div className="sidebar-subtitle">Templates and saves</div>
        </div>
        {!isNarrow && (
          <button
            className="sidebar-toggle-btn"
            onClick={onToggleSidebar}
            aria-label="Collapse loop menu"
          >
            Hide
          </button>
        )}
      </div>

      <div className="sidebar-actions-panel">
        <div className="sidebar-primary-actions">
          {canSave && readOnlyVersion === null && (
            <>
              <button className="save-btn" onClick={onSave} disabled={isSaving}>
                {isSaving ? "Saving..." : "Save"}
              </button>
              <button className="export-btn" onClick={onExport} title="Export template to JSON">
                ⬇ Export
              </button>
            </>
          )}
          <div className="sidebar-secondary-actions">
            <button className="new-template-btn" onClick={onCreateTemplate}>
              New Template
            </button>
            <button className="import-btn" onClick={onImport} title="Import template from JSON">
              ⬆ Import
            </button>
          </div>
        </div>
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
      </div>

      <div className="loop-list">
        {!showVersionHistory ? (
          <>
            <div className="loop-list-controls">
              <label className="show-archived-toggle">
                <input
                  type="checkbox"
                  checked={showArchived}
                  onChange={(event) => onShowArchivedChange(event.target.checked)}
                />
                Show archived
              </label>
            </div>
            {visibleTemplates.map((template) => (
              <div
                key={template.id}
                className={`loop-list-item ${selectedTemplateId === template.id ? "active" : ""} ${template.isArchived ? "archived" : ""}`}
                onClick={() => onSelectTemplate(template)}
              >
                <div className="loop-list-item-name">
                  {template.name}
                  {template.isArchived && <span className="archived-badge">Archived</span>}
                </div>
                <div className="loop-list-item-meta">
                  v{template.version} &middot; {template.nodes.length} nodes
                </div>
                {cloningTemplateId === template.id ? (
                  <div className="clone-input-row">
                    <input
                      type="text"
                      className="clone-name-input"
                      placeholder="Clone name"
                      value={cloneName}
                      onClick={(event) => event.stopPropagation()}
                      onChange={(event) => onCloneNameChange(event.target.value)}
                      onKeyDown={(event) => {
                        if (event.key === "Enter") onConfirmClone(template);
                      }}
                    />
                    <button
                      className="clone-confirm-btn"
                      onClick={(event) => {
                        event.stopPropagation();
                        onConfirmClone(template);
                      }}
                    >
                      Clone
                    </button>
                  </div>
                ) : (
                  <div className="loop-list-item-actions">
                    <button
                      className="archive-btn"
                      onClick={(event) => {
                        event.stopPropagation();
                        onToggleArchive(template);
                      }}
                    >
                      {template.isArchived ? "Unarchive" : "Archive"}
                    </button>
                    <button
                      className="clone-btn"
                      onClick={(event) => {
                        event.stopPropagation();
                        onStartClone(template);
                      }}
                    >
                      Clone
                    </button>
                    <button
                      className="versions-btn"
                      onClick={(event) => {
                        event.stopPropagation();
                        onShowVersionHistory(template);
                      }}
                    >
                      Versions
                    </button>
                  </div>
                )}
              </div>
            ))}
          </>
        ) : (
          <div className="version-history-list">
            <div className="version-history-header">
              <span>Version History</span>
              <button className="back-to-templates-btn" onClick={onBackToTemplates}>
                ← Back
              </button>
            </div>
            {versionHistory.map((version) => (
              <div
                key={version.id}
                className="version-history-item"
                onClick={() => onSelectVersion(version.loopTemplateId, version.versionNumber)}
              >
                <div className="version-number">v{version.versionNumber}</div>
                <div className="version-meta">
                  {new Date(version.createdAt).toLocaleDateString()} &middot;{" "}
                  {version.nodeCount ?? "-"} nodes
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </aside>
  );
}
