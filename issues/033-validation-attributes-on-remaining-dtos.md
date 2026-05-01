## What to build

#23 added validation attributes to `AiProviderDto` and `WorkItemCreateRequest`. The remaining write DTOs are still un-annotated and rely on `[ApiController]`'s implicit checks plus controller logic.

DTOs to audit:

- `LoopTemplateCreateRequest` / `UpdateRequest`
- `RepositoryCreateRequest` / `UpdateRequest`
- `RemoteProviderCreateRequest` / `UpdateRequest`
- `WorkItemUpdateRequest`, `WorkItemTransitionRequest`, `WorkItemDependencyRequest`
- `LoginRequest`, any auth/token DTOs
- Webhook payload DTOs

Add `[Required]`, `[StringLength]`, `[Url]`, `[EmailAddress]`, `[Range]`, etc. as appropriate, plus `[RegularExpression]` where the string has a known shape (slug, hex SHA, semver).

## Acceptance criteria

- [x] Every POST/PUT DTO has at least one validation attribute or an explicit comment justifying its absence
- [x] Invalid payloads return `400` with the field-level errors (verified by at least one negative test per DTO)
- [x] Existing tests still pass

## Blocked by

None. Spun out of #023 part C.
