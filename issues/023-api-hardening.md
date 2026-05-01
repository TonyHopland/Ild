## What to build

API surface lacks pagination, validation, and basic security hygiene.

**A — Pagination on list endpoints:** [WorkItemsController.GetAll](ILD.Api/Controllers/WorkItemsController.cs#L25), [LoopTemplatesController.GetAll](ILD.Api/Controllers/LoopTemplatesController.cs#L15), [AiProvidersController.GetAll](ILD.Api/Controllers/AiProvidersController.cs#L18), and similar are unbounded. Add `skip`/`take` (or cursor) with sensible defaults.

**B — Cap event-log `limit`:** [LoopRunsController.GetEvents](ILD.Api/Controllers/LoopRunsController.cs#L61) accepts arbitrary `limit`. Cap to e.g. 500.

**C — DTO validation attributes:** Input DTOs in `ILD.Data/DTOs/` (e.g. `AiProviderDto`, `WorkItemDto`, `LoopTemplateDto`) have no `[Required]`, `[StringLength]`, `[Url]`, or `[Range]`. Add them to all create/update request models.

**D — `ModelState.IsValid` on every POST/PUT:** Only `LoopTemplatesController.Create` checks it today. Either add explicit checks or apply a global `[ApiController]`-style filter. Confirm `[ApiController]` is set on every controller (it auto-validates) and remove redundant manual checks.

**E — Security middleware:** [Program.cs](ILD.Api/Program.cs) has no `UseHsts`, `UseHttpsRedirection`, or security headers. Add HSTS in non-development environments and a small middleware setting `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, and a CSP appropriate for the SPA.

**F — API versioning:** Routes use `/api/v1/...` but there is no `Microsoft.AspNetCore.Mvc.Versioning` (or `Asp.Versioning`) wiring. Either add the package and `[ApiVersion("1.0")]` attributes or document the manual versioning policy in `CONTEXT.md`.

## Acceptance criteria

- [x] All list endpoints accept and honour `skip`/`take` (or cursor) with documented defaults and max values — _done in #032_
- [x] Event-log `limit` is capped server-side
- [x] Every input DTO used in POST/PUT has appropriate validation attributes — _done in #033_
- [x] `[ApiController]` is on all controllers (or `ModelState.IsValid` is checked manually) so invalid payloads return 400
- [x] Security headers middleware is registered; HSTS active outside Development
- [x] API versioning strategy is implemented or explicitly documented

## Blocked by

None.
