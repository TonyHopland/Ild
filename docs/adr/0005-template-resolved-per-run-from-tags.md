# ADR-0005: Loop template is resolved per-run from WorkItem tags, not stored on the WorkItem

A WorkItem is not linked to a LoopTemplate. At run start the engine resolves a template by matching `WorkItem.Tags` against templates via `ILoopTemplateResolver` (tags must match exactly one — zero or multiple matches send the item to HumanFeedback), then pins that template's latest version on the new LoopRun. We chose tag-based resolution so work routing is data-driven and a template edit takes effect on the next run without touching every work item, rather than freezing a template choice onto the WorkItem at creation time.

## Consequences

- Editing a template between runs is picked up automatically on the next run; the in-flight run is unaffected because it pinned its version.
- Tag/template hygiene matters: an ambiguous or unmatched tag set is a human-feedback condition, not a silent default.
