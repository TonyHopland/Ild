import type { DragEvent } from "react";
import { NodeType, type LoopTemplate } from "../../../types";
import type { LoopTemplateVersion } from "../types";

interface PaletteItem {
  type: NodeType;
  label: string;
}

interface LoopEditorSidebarProps {
  readOnlyVersion: number | null;
  paletteItems: PaletteItem[];
  showArchived: boolean;
  templates: LoopTemplate[];
  selectedTemplateId: string | null;
  cloningTemplateId: string | null;
  cloneName: string;
  showVersionHistory: boolean;
  versionHistory: LoopTemplateVersion[];
  onPaletteDragStart: (nodeType: NodeType, event: DragEvent<HTMLDivElement>) => void;
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
  readOnlyVersion,
  paletteItems,
  showArchived,
  templates,
  selectedTemplateId,
  cloningTemplateId,
  cloneName,
  showVersionHistory,
  versionHistory,
  onPaletteDragStart,
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
    <>
      <div className={`node-palette ${readOnlyVersion !== null ? "palette-disabled" : ""}`}>
        <div className="palette-header">Drag &amp; Drop</div>
        {paletteItems.map((item) => (
          <div
            key={item.type}
            className="palette-item"
            draggable={readOnlyVersion === null}
            onDragStart={(event) => onPaletteDragStart(item.type, event)}
          >
            {item.label}
          </div>
        ))}
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
    </>
  );
}
