# Provider × login gateway matrix

Spike for the run-hosts series: can each AI provider's CLI reach its model through an ILD credential gateway while the run holds only a **placeholder** credential? The gateway checks the per-run token, swaps the placeholder for the real credential and forwards over its own TLS connection. There is no TLS interception.

Everything below was run live in the agent pod on 2026-11-06. No ILD code changed. The relay, the fake upstream and the scratch config directories lived in `/tmp` and are gone.

## Verdict

**There is no blocker.** Every adapter and login type has a working path. None needs the read-only-copy fallback to function, but two rows need a gateway that does more than swap a header (see "What the gateway has to be").

| Provider (CLI version) | Login | Result | How the CLI is pointed at the gateway |
|---|---|---|---|
| Claude Code 2.1.318 | Subscription (OAuth) | **Gateway works** | `ANTHROPIC_BASE_URL` + placeholder `CLAUDE_CODE_OAUTH_TOKEN` |
| Claude Code 2.1.318 | API key | **Gateway works** | `ANTHROPIC_BASE_URL` + placeholder `ANTHROPIC_API_KEY` |
| Codex 0.167.0 | ChatGPT subscription | **Gateway works**, with a custom `model_providers` entry | `-c chatgpt_base_url=…` + `-c model_providers.<id>={…}` + placeholder `auth.json` |
| Codex 0.167.0 | API key | **Gateway works** | `model_providers.<id>` with `base_url` + `env_key`, placeholder `OPENAI_API_KEY` |
| Copilot 1.0.105 | GitHub login | **Gateway works**, but HTTPS-only and the gateway must rewrite one JSON response | `GH_HOST=<ILD's run-facing host:HTTPS port>` + placeholder `COPILOT_GITHUB_TOKEN` + `NODE_EXTRA_CA_CERTS` (a copy of the certificate staged into the run) |
| OpenCode 1.19.5 | Base URL + key | **Gateway works** | `provider.<id>.options.baseURL` / `apiKey` (what the adapter already writes) |
| Pi 1.5.0 | Base URL + key | **Gateway works** | `models.json` `baseUrl` / `apiKey` (what the adapter already writes) |

What the adapters expose today: `ClaudeCodeAdapter`, `CodexAdapter` and `CopilotAdapter` are subscription-only and ignore `AiProvider.BaseUrl` and `ApiKey`. The two "API key" rows for Claude and Codex show what the CLIs can do, not what ILD configures now. OpenCode and Pi already take a base URL and key from the provider.

### What the gateway has to be

1. **Not just a header swap for Codex.** The built-in ChatGPT provider talks to `/codex/responses` over a **WebSocket upgrade**. A plain HTTP relay hangs on that request. Either the gateway also proxies WebSocket upgrades, or the run config declares a custom provider with `supports_websockets=false`, which makes Codex use streamed HTTP (SSE) and was verified here.
2. **TLS plus response rewriting for Copilot.** Copilot only accepts `GH_HOST` as an HTTPS endpoint. At start it calls `GET https://<GH_HOST>/copilot_internal/user`, and the JSON answer names the real model endpoints (`api.individual.githubcopilot.com`, `telemetry.…`, `exp.…`, `origin-tracker.…`). The gateway must rewrite those URLs to itself, for example `https://<gateway>/_ep/<host>/…`, and route by that prefix. The CLI must trust the gateway's certificate (`NODE_EXTRA_CA_CERTS`). This is the gateway's own server certificate, not an interception CA.
3. **Placeholders must look like the real thing.** Copilot rejects a token without a recognised prefix before making any request (`gho_…` works, `ild-placeholder-gh` does not). Codex needs `auth.json` to hold a JWT-shaped `id_token`. Claude, OpenCode and Pi accept any string.
4. **Direct hosts must be allowed to fail.** Several CLIs make side calls to hosts that are not the model endpoint (table below). In every case the run still worked with direct egress blocked, so the egress proxy can deny them.
5. **Streaming must pass through unbuffered.** All the agent turns used streamed responses (SSE).

## Method

- **Relay**: about 100 lines of Node on `127.0.0.1`. It logs method, path, which auth header arrived and whether it held the placeholder, request flags (`stream`, tool count), response status, content type and chunk count. It then replaces the placeholder with the real value in `Authorization`, `x-api-key` or `api-key`, forwards upstream through the pod egress proxy and streams the response back. Tokens are redacted from every log line.
- **Real credentials**: only the *access* token of the shared Claude, Codex and Copilot logins was used, read by the relay. Refresh tokens were never sent or used. Each CLI ran with a scratch `HOME` and `CLAUDE_CONFIG_DIR` / `CODEX_HOME` / `COPILOT_HOME` / `PI_CODING_AGENT_DIR` that held only placeholders. The shared `.agent-config` was not written.
- **Keyed API rows** (no real Anthropic or OpenAI key exists for agents): a fake upstream accepted only one made-up key (`401` otherwise) and proxied to LM Studio (`qwen3.8-27b`). The chain was CLI → relay (swap) → fake upstream (checks the key) → LM Studio. The Claude API-key row used the real subscription token as the "real" credential: the relay swapped the `x-api-key` placeholder for `Authorization: Bearer` plus `anthropic-beta: oauth-2025-04-20` to reach `api.anthropic.com`. That proves the swap and streaming, not Anthropic's key-auth server.
- **Stray hosts**: the CLI's `HTTPS_PROXY` pointed at a logging shim that records `CONNECT` targets and chains to the pod proxy. Then `HTTPS_PROXY` was pointed at a dead port to prove the run works with every direct host blocked.
- **Each row**: one plain prompt, then an agent turn that reads `note.txt`, runs `echo gateway-ok > out.txt` and reports. The file was checked afterwards.

## Rows

### Claude Code, subscription (OAuth): gateway works

```
HOME=<scratch>  CLAUDE_CONFIG_DIR=<scratch>/.claude
ANTHROPIC_BASE_URL=http://127.0.0.1:4001
CLAUDE_CODE_OAUTH_TOKEN=ild-placeholder-claude-oauth
claude -p "…" --allowedTools Read Bash
```

Relay log (redacted), one line per model call of the tool turn:

```
POST /v1/messages?beta=true  auth: authorization = "Bearer <PLACEHOLDER>"  stream=true  model=claude-sonnet-5-5  tools=12
  beta: claude-code-20250219,oauth-2025-04-20,…   → 200 text/event-stream
POST /v1/messages  Bearer <PLACEHOLDER>  stream=true → 200 text/event-stream  (×3 for the tool turn)
```

CLI output: plain prompt `PONG`; tool turn `note.txt said: hello-from-file … ran echo gateway-ok > out.txt`, and `out.txt` held `gateway-ok`.

Also verified:

- A raw non-streaming `POST /v1/messages` (`stream:false`, `Bearer <PLACEHOLDER>`) through the relay returned `200` and `PONG`. Anthropic rejects such requests (`429 rate_limit_error`) unless the system prompt is the Claude Code one. That is Anthropic's rule, not a gateway problem, and the CLI sends the right prompt itself.
- A credentials **file** (`<config>/.credentials.json`, `claudeAiOauth.accessToken` = placeholder, `expiresAt` = now + 6 h, with or without a `refreshToken`) works the same way. An `expiresAt` of year 2100 was rejected with "OAuth session expired and could not be refreshed", so a file copy needs a realistic expiry.
- **No refresh when the token comes from the environment.** With a wrong token the CLI printed `Failed to authenticate. API Error: 401 Invalid bearer token` and made no call to any auth host. An expired credentials file (`expiresAt: 1000`) instead tries `CONNECT platform.claude.com:443` directly and fails with "OAuth session expired and could not be refreshed". So refresh does not go through `ANTHROPIC_BASE_URL`; with a placeholder it fails closed.
- **Long-lived token form**: `CLAUDE_CODE_OAUTH_TOKEN` is the non-refreshing form (documented as the output of `claude setup-token`). I could not run `setup-token` live because it needs an interactive browser login. The environment-variable path itself was verified above with the shared login's access token. That token expires within hours (about 5.6 h were left on the one used), so a long run needs the gateway to read the current token from the store at request time. A `setup-token` token avoids that.
- **Extra hosts**: without `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` the CLI opened 6 direct TLS connections to `api.anthropic.com:443` (bootstrap, event logging and similar) that ignore `ANTHROPIC_BASE_URL`. They carry only the placeholder. Blocked, the run still passed (3.1 s for the plain prompt, tool turn OK). With the flag set, no direct host was contacted at all. **Set it in the run.**

### Claude Code, API key: gateway works

```
ANTHROPIC_BASE_URL=http://127.0.0.1:4002   ANTHROPIC_API_KEY=ild-placeholder-claude-key
claude -p "Read note.txt and say what it contains in one line." --allowedTools Read
```

```
POST /v1/messages?beta=true  auth: x-api-key = <PLACEHOLDER>  stream=true  → 200      (×2: tool call, final answer)
```

CLI output: ``note.txt contains a single line: `hello-from-file`.`` The key arrives unchanged in `x-api-key`, so a key swap works. Same extra-host caveat as above.

### Codex, ChatGPT subscription: gateway works (custom provider, no WebSocket)

```
CODEX_HOME=<scratch>   # auth.json: auth_mode=chatgpt, access_token=ild-placeholder-codex-access,
                       # refresh_token=ild-placeholder-codex-refresh, JWT-shaped id_token, recent last_refresh
codex exec --skip-git-repo-check \
  -c chatgpt_base_url=http://127.0.0.1:4003 \
  -c 'model_providers.ild={name="ild",base_url="http://127.0.0.1:4003/codex",wire_api="responses",requires_openai_auth=true,supports_websockets=false}' \
  -c model_provider=ild "…"
# relay upstream: https://chatgpt.com/backend-api
```

```
POST /codex/responses            Bearer <PLACEHOLDER>  stream=true → 200   (×3 for the tool turn)
GET  /codex/models               Bearer <PLACEHOLDER>  → 200
GET  /ps/plugins/list|installed  Bearer <PLACEHOLDER>  → 200
POST /codex/analytics-events/events  Bearer <PLACEHOLDER> → 200
GET  /api/codex/accounts/check   Bearer <PLACEHOLDER>  → 404
```

CLI output: `PONG`, then the tool turn ran `cat note.txt` and `echo gateway-ok > out.txt` and answered ``note.txt said: `hello-from-file` ``. The run passed with direct egress blocked.

Caveats:

- `chatgpt_base_url` alone is not enough. The model list and responses use the *provider's* `base_url`: with only `chatgpt_base_url`, `GET https://chatgpt.com/backend-api/codex/models` went **direct** with the placeholder and got `401 Could not parse your authentication token`. The custom `model_providers` entry with `requires_openai_auth=true` makes Codex reuse the ChatGPT login against the gateway.
- **WebSocket**: with the built-in provider (`-c openai_base_url=…/codex` instead of the custom entry) Codex issued `GET /codex/responses` as a WebSocket upgrade. The plain relay hung until Codex printed "Falling back from WebSockets to HTTPS transport". Use `supports_websockets=false`, or make the gateway proxy upgrades.
- **Refresh**: the access token lasts about 10 days and Codex tries to refresh once `last_refresh` is old (the 2026-10-01 test below triggered it; the exact threshold was not measured). With a placeholder `auth.json` whose `last_refresh` was backdated to 2026-10-01, the CLI tried `CONNECT auth.openai.com:443` **directly** four times, logged `Failed to refresh token: Your access token could not be refreshed`, and **still completed the turn** through the gateway. The binary also names `CODEX_REFRESH_TOKEN_URL_OVERRIDE`, which would let the gateway handle refresh. I did not exercise it.
- **Extra hosts, no credential**: `ab.chatgpt.com` (feature flags) and `*.oaiusercontent.com` (plugin assets). Both can be blocked.
- **Placeholder shape**: `id_token` needs three dot-separated parts with a decodable payload; the access token can be any string.

### Codex, API key: gateway works

```
OPENAI_API_KEY=ild-placeholder-apikey   CODEX_HOME=<empty scratch>
codex exec -m qwen3.8-27b \
  -c 'model_providers.ild={name="ild",base_url="http://127.0.0.1:4011/v1",wire_api="responses",env_key="OPENAI_API_KEY",supports_websockets=false}' \
  -c model_provider=ild "…"
```

Relay and fake upstream:

```
relay:    POST /v1/responses  Bearer <PLACEHOLDER>  stream=true → 200   (×2: tool call, answer)
upstream: POST /v1/responses  keyOk=true  stream=true  tools=9
```

CLI output: `PONG`; the tool turn left `out.txt` = `gateway-ok2` and answered ``Done — `out.txt` now contains `gateway-ok2`, and note.txt said: **hello-from-file**``. `keyOk=true` shows the upstream received the swapped key, not the placeholder. Direct hosts seen: `chatgpt.com`, `ab.chatgpt.com`, `github.com` (plugin and feature metadata, no key). Note: a first run showed Codex stuck on "Reconnecting… waiting for network" when the relay itself had crashed, so a gateway outage looks like a hang, not an error.

### Copilot, GitHub login: gateway works (HTTPS, with response rewriting)

```
COPILOT_HOME=<scratch>   HOME=<scratch>
COPILOT_GITHUB_TOKEN=gho_ILDPLACEHOLDER000000000000000000000
GH_HOST=127.0.0.1:4005                 # must be HTTPS, host:port form
NODE_EXTRA_CA_CERTS=<gateway-cert.pem>
copilot -p "…" --allow-all-tools --no-auto-update
# gateway: TLS on :4005, upstream https://api.github.com, rewrites https://*.githubcopilot.com in JSON
# responses to https://127.0.0.1:4005/_ep/<host>, and routes /_ep/<host>/… to https://<host>/…
```

```
GET  /copilot_internal/user                          Bearer <PLACEHOLDER> → 200   (response rewritten: api.individual / telemetry.individual / exp.individual / origin-tracker.individual .githubcopilot.com)
GET  /_ep/api.individual.githubcopilot.com/models     Bearer <PLACEHOLDER> → 200
POST /_ep/api.individual.githubcopilot.com/v1/messages Bearer <PLACEHOLDER> stream=true → 200  (×2 for the tool turn)
POST /_ep/telemetry.individual.githubcopilot.com/telemetry  (no credential) → 200  (×3)
GET  /copilot_internal/managed_settings, /team_customizations, POST /mcp/readonly → 404
```

CLI output: `PONG`; the tool turn printed `● Read note.txt`, `● Write gateway-ok to out.txt (shell)` and answered ``note.txt said: `hello-from-file` ``. A fresh `COPILOT_HOME` with `HTTPS_PROXY` pointing at a dead port also passed, so nothing needs a direct host.

Caveats:

- No other URL override exists for the GitHub login. `GH_HOST` is the lever: a bare hostname makes Copilot use `https://api.<host>`, and `host:port` makes it use `https://host:port/copilot_internal/user`. A plain `http://` value is turned into `https://api.http/…` and fails. `COPILOT_PROVIDER_BASE_URL` (BYOK) exists but replaces Copilot's model routing and the subscription entirely, so it is not this login.
- The placeholder must pass the CLI's own prefix check (`gho_…` was verified; other prefixes were not tried). `ild-placeholder-gh` fails locally with "No authentication information found" before any request.
- **Token exchange**: Copilot does **not** exchange the GitHub token for a short-lived Copilot token here. It sends the same GitHub token as `Authorization: Bearer` to `/copilot_internal/user` and to the model endpoints. A header swap suffices once the endpoints are rewritten. Only an individual plan was tested; other plans probably return other endpoint hosts, so the rewrite should match `*.githubcopilot.com` generally.
- **Extra hosts without the flag**: `api.github.com` (2 connections), `github.com` and `release-assets.githubusercontent.com` (auto-update download). Set `--no-auto-update`.

### OpenCode, base URL + key: gateway works

Config as `OpenCodeAdapter.BuildOpenCodeConfig` writes it, passed in `OPENCODE_CONFIG_CONTENT`:

```json
{"provider":{"ildlm":{"npm":"@ai-sdk/openai-compatible","name":"ildlm",
  "options":{"baseURL":"http://127.0.0.1:4011/v1","apiKey":"ild-placeholder-apikey"},
  "models":{"qwen3.8-27b":{"name":"qwen3.8-27b"}}}}, "permission":{…allow…}}
opencode run --dir <worktree> --model ildlm/qwen3.8-27b --format json "…"
```

```
relay:    POST /v1/chat/completions  Bearer <PLACEHOLDER>  stream=true  tools=10 → 200 text/event-stream  (×2, plus one title request with no tools)
upstream: keyOk=true on all three
```

CLI output: `tool_use read` (`note.txt`), `tool_use bash` (`echo gateway-ok4 > out.txt`), final `text` ``note.txt said: …``; `out.txt` held `gateway-ok4`. A raw non-streaming `POST /v1/chat/completions` (`stream:false`) through the relay also returned `200`.

Caveat: hosts seen with the proxy logger were `models.opencode.ai` (model catalog, no credential) and `registry.npmjs.org` (a fresh scratch home installs `@ai-sdk/openai-compatible` on first use). A run host needs the package preinstalled or the registry allowed. There is no refresh because there is no OAuth.

### Pi, base URL + key: gateway works

`PI_CODING_AGENT_DIR/models.json` as `PiAdapter.BuildModelsJson` writes it, with `ILD_PI_PROVIDER_API_KEY=ild-placeholder-apikey` in the environment:

```json
{"providers":{"ild-lm":{"baseUrl":"http://127.0.0.1:4011/v1","api":"openai-completions",
  "apiKey":"$ILD_PI_PROVIDER_API_KEY","models":[{"id":"qwen3.8-27b"}]}}}
pi -p --mode json --provider ild-lm --model qwen3.8-27b "…"      # PI_SKIP_VERSION_CHECK=1 PI_TELEMETRY=0
```

```
relay:    POST /v1/chat/completions  Bearer <PLACEHOLDER>  stream=true  tools=4 → 200 text/event-stream  (×2)
upstream: keyOk=true on both
```

CLI output: the JSON event stream ended with the assistant message ``…said: `hello-from-file`… Ran `echo gateway-ok3 > out.txt` — succeeded``; `out.txt` held `gateway-ok3`. **No host other than the gateway was contacted.** No refresh.

## Fallback: read-only per-run copy

No row requires it to work, so each login's gateway path is the recommended one. Where a copy is wanted anyway (for example a CLI that stays unconfigured), these are the findings that bear on it:

| Login | Copy contents | Concern |
|---|---|---|
| Claude subscription | `CLAUDE_CODE_OAUTH_TOKEN` = current **access** token only | Safe: no refresh token leaves ILD, and the env form never refreshes (verified). Expires within hours, so the copy must be re-minted per run, or use a `setup-token` token. |
| Claude subscription | `.credentials.json` with the refresh token | Risky: the CLI refreshes direct to `platform.claude.com` when expired and would rotate the refresh token without being able to write it back. Not tested live because it could invalidate the shared login. Avoid. |
| Codex ChatGPT | `auth.json` with the refresh token | Same rotation risk once `last_refresh` is old. A run copy with a fresh `last_refresh` and no useful refresh token behaves like the placeholder case above (refresh fails, the turn continues until the access token expires at about 10 days). |
| Copilot | `COPILOT_GITHUB_TOKEN` = the `gho_` token | No refresh. The token is long-lived and fully readable by the agent. |
| OpenCode, Pi | provider `ApiKey` | Static key; only the gateway keeps it out of the run. |

## Reproducing

All of this is throwaway code and not in the repo. To redo it: run a loopback relay that swaps the placeholder in the three auth headers and streams the upstream body through; give each CLI a scratch config directory with the placeholder; and set `HTTPS_PROXY` to a logging shim, then to a dead port, to see which hosts it needs. The Copilot row also needs a TLS listener and a response rewriter as described under "What the gateway has to be".
