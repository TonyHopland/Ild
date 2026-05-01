## What to build

Three security issues in the API layer:

**A — CORS allows all origins with credentials:** `Program.cs` sets `SetIsOriginAllowed(_ => true)` with `AllowCredentials()`. Any website can make authenticated requests. Restrict to configured origins via `ILD_ALLOWED_ORIGINS` env var (comma-separated, defaults to `http://localhost:3000,http://localhost:5173`).

**B — Webhook endpoint has no signature verification:** `POST /api/v1/webhooks/forgejo` accepts any POST without verifying a webhook secret. The `RemoteProvider.WebhookSecret` field already exists but is never validated. Add HMAC-SHA256 signature verification against the `X-Forgejo-Signature` header (Forgejo/Gitea standard). Match the webhook to the correct RemoteProvider by repository.

**C — AI provider API keys exposed in API responses:** `AiProvidersController.GetAll` and `GetById` return the full `AiProvider` entity including the `Config` JSON blob (which contains API keys). Redact sensitive fields in responses.

**Decision logged:** CORS uses `ILD_ALLOWED_ORIGINS` env var. Webhook uses per-RemoteProvider `WebhookSecret` with HMAC-SHA256 (Forgejo standard `X-Forgejo-Signature` header).

## Acceptance criteria

- [x] CORS policy restricts to origins from `ILD_ALLOWED_ORIGINS` env var (defaults to localhost:3000, localhost:5173)
- [x] Webhook endpoint validates HMAC-SHA256 signature from `X-Forgejo-Signature` header against `RemoteProvider.WebhookSecret`
- [x] Webhook is matched to the correct RemoteProvider by the repository in the payload
- [x] `AiProvider` API responses redact the `Config` field (or return a DTO without it)
- [x] Existing webhook flow still works with correct secret configured

## Blocked by

None - can start immediately
