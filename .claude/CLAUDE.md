<!-- GSD:project-start source:PROJECT.md -->
## Project

**PlanMeter**

A Windows desktop, always-on-top **compact floating widget** that aggregates and displays the current-period **quota usage** of the LLM coding plans you've subscribed to or logged into — Codex, Grok, Z.ai, OpenCode GO, MiniMax. It auto-detects locally-logged-in providers and reuses their existing credentials, falling back to a manually-entered API key when a provider can't be auto-detected. Goal: see how much quota each plan has used / has left this period, at a glance, without opening five separate provider websites.

**Core Value:** At a glance, see each coding plan's current-period **quota used / remaining** — so you never get surprised by a depleted plan mid-task.

### Constraints

- **Platform**: Windows only for v1 — narrows stack to a Windows-capable desktop framework (Electron / Tauri / native Win32-WPF-WinUI); research will recommend.
- **Always-on-top**: the widget must float above all windows (OS-level topmost window) without stealing focus.
- **Auth model (zero-config ideal)**: must reuse the user's existing local login state wherever possible — must not require re-authentication for already-logged-in providers.
- **Provider risk**: polling must stay conservative (~10 min) to avoid rate-limiting / risk-control on session-reuse providers.
- **Per-provider adapters**: each provider needs its own usage-fetch adapter; some providers may expose **no programmatic usage access at all** and must be marked unsupported rather than faked.
<!-- GSD:project-end -->

<!-- GSD:stack-start source:research/STACK.md -->
## Technology Stack

## TL;DR — Prescriptive Recommendation
## Recommended Stack
### Core Technologies
| Technology | Version | Purpose | Why Recommended |
|------------|---------|---------|-----------------|
| **.NET SDK** | **8.0 LTS** (`net8.0-windows`) | Runtime + build | Current LTS with long support window; `net8.0-windows` TFM enables WPF. (.NET 9 is STS / shorter support; .NET 10 LTS is the natural upgrade path later but 8 is the safe 2026 baseline.) |
| **WPF** | shipped with .NET 8 (`PresentationFramework`) | UI shell, topmost floating window | Mature, one-line `Topmost="True"` that "just works"; borderless/transparent window support is battle-tested; no native-runtime packaging trap like WinUI 3. |
| **C#** | 12 (with .NET 8) | Implementation language | First-class Windows/HTTP/JSON ergonomics; strong static typing for the per-provider adapter layer. |
| **HttpClient** (`System.Net.Http`) | built-in | Per-provider usage polling | One factory-managed client per provider; full control over headers, cookies, bearer tokens, OAuth refresh — exactly what mixed auth (session-reuse vs API-key) requires. |
| **System.Text.Json** | built-in | Parse provider responses | No extra dep; source-generated serializers keep allocation low for an always-on app. |
### Supporting Libraries
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| **`Microsoft.Extensions.Hosting`** | 8.0.x | App lifecycle / DI / hosted background service | Recommended — gives a clean `IHostedService` for the 10-min polling loop and DI for the per-provider adapter registry. Keeps the polling off the UI thread. |
| **`Microsoft.Extensions.Http`** | 8.0.x | `IHttpClientFactory` with per-provider named clients | Use to attach resilience (short timeout, retry policy) and to isolate cookie jars per provider (Codex/MiniMax session reuse must not cross-contaminate). |
| **`Polly`** | 8.x | Retry + circuit breaker for provider calls | Use conservatively: 1 retry on transient failure, short timeout. **Avoid aggressive retries** — PROJECT.md flags rate-limit / risk-control on session-reuse providers. A failed poll must degrade the UI, not hammer the provider. |
| **`Serilog`** (or `Microsoft.Extensions.Logging`) | 4.x / 8.x | Structured logging | **Mandatory sanitization point** — see Credential Security below. Logging is fine for adapter errors/latency, but a custom redactor sink MUST strip any token/cookie/key before write. Default: log only provider name + status code + timing, never headers/bodies. |
| **`Dapper`** (optional) | 2.x | Tiny SQLite cache for last-known-good values | Only if you want to show stale-but-recent values when a poll fails or the app restarts offline. Not needed for v1. |
### Development Tools
| Tool | Purpose | Notes |
|------|---------|-------|
| **Visual Studio 2022 17.10+** or **`dotnet` CLI + VS Code** | Build / debug | CLI is enough for a small widget; VS gives better WPF XAML designer. |
| **`dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true`** | Produce one portable EXE | The recommended distribution artifact. ~60–80 MB self-contained, trim if you can. No installer needed; user drops it anywhere. |
| **WiX Toolset v3 / Inno Setup** (optional) | Installer / autostart-on-login shortcut | Only if you want Start Menu + `HKCU\Run` autostart wiring. For a personal utility, a single EXE + a "run at startup" checkbox in settings is enough. |
| **`HTTP Toolkit`** or **Fiddler Classic** | Inspect provider traffic during adapter dev | Indispensable for reverse-engineering the session-reuse endpoints (Codex `wham/usage`, MiniMax `coding_plan/remains`). |
## Per-Provider Credential Storage & Access Verdict (Windows)
### Confidence legend
- **HIGH** = confirmed by ≥2 independent sources incl. official docs or the provider's own client source.
- **MEDIUM** = confirmed by ≥1 working third-party implementation (CodexBar / opencode-mystatus) but endpoint is undocumented/undocumented-by-vendor.
- **LOW** = single community source or conflicting reports.
### 1. OpenAI Codex (ChatGPT subscription login) — **REACHABLE (HIGH)**
| Item | Value |
|---|---|
| **Cred file (Windows)** | `%USERPROFILE%\.codex\auth.json` (override: `CODEX_HOME` env var) |
| **Auth type** | **OAuth** — `tokens.id_token` (JWT), `tokens.access_token` (Bearer), `tokens.refresh_token`. (API-key mode instead stores `OPENAI_API_KEY`.) |
| **Usage endpoint (subscription)** | `https://chatgpt.com/backend-api/wham/usage` (Codex CLI itself polls this — see openai/codex#10869) and the codex backend `https://chatgpt.com/backend-api/codex/responses` returns rate-limit headers. |
| **Auth header** | `Authorization: Bearer <access_token>` (the OAuth access token from `auth.json`) |
| **What you get** | 5-hour rolling window usage + weekly cap consumption; CodexBar + opencode-mystatus both render this as a quota %. |
| **Programmatic access verdict** | **YES — HIGH confidence.** The Codex CLI's own traffic proves the endpoint; two shipping third-party apps consume it. |
| **Caveat** | Undocumented/internal endpoint — subject to change without notice. OAuth `access_token` is short-lived (~hours); **must implement refresh using `refresh_token`** before reuse, and never run refresh on a hot path (rotates the refresh token and can invalidate the user's actual Codex CLI session — see LangChain `chatgpt_oauth` caution). |
### 1b. OpenAI (pay-as-you-go API key) — **REACHABLE (HIGH)**
| Item | Value |
|---|---|
| **Cred file** | `%USERPROFILE%\.codex\auth.json` (`OPENAI_API_KEY` field) **or** env var `OPENAI_API_KEY` |
| **Auth type** | API key (`sk-...`). Usage/Costs endpoints require an **Admin API key** (`sk-admin-...`). |
| **Usage endpoint** | `GET https://api.openai.com/v1/organization/costs` and `/v1/organization/usage/completions` (old `/v1/dashboard/billing/usage` is **deprecated/removed**). |
| **Auth header** | `Authorization: Bearer sk-admin-...` |
| **Programmatic access verdict** | **YES — HIGH confidence.** Official docs. Note: returns **$ cost / token count**, not a "quota %". For pay-as-you-go there is no per-period quota by design — only a spend total. Display this provider as "spend this period ($)" not "quota %", or skip the % for it. |
| **Caveat** | Standard `sk-` keys get 403; you need an org Admin key the user must create at `platform.openai.com/settings/organization/admin-keys`. Intermittent 404s reported on the endpoint (community). |
### 2. xAI Grok — **PARTIALLY REACHABLE (MEDIUM)**
| Item | Value |
|---|---|
| **Cred file** | No first-party CLI standard env. Grok Build CLI (xAI's official coding agent) stores `~/.grok/auth.json` with refreshable access tokens — on Windows: `%USERPROFILE%\.grok\auth.json`. API-key users keep `XAI_API_KEY` in env or a tool config (`.env`, `config.toml`). |
| **Auth type** | **Two surfaces:** (a) **API key** (`xai-...`) for `https://api.x.ai/v1`; (b) **OAuth session** for Grok Build CLI. |
| **Usage endpoint** | API key → console.x.ai billing dashboard only; **no clearly documented public usage/quota REST endpoint** found. Usage is surfaced via the console UI and rate-limit headers on responses. |
| **Programmatic access verdict** | **MAYBE — MEDIUM confidence.** A billing/usage endpoint likely exists (CodexBar lists Grok as a supported provider with quota tracking), but the public, stable, documented endpoint for programmatic quota% was not confirmed in official xAI docs. |
| **Recommendation** | Treat Grok as **session-reuse best-effort**: read rate-limit headers from a lightweight probe call to `/v1/models` or `/v1/chat/completions` (if `x-ratelimit-*` headers are returned) and derive remaining% from headers. Flag as "estimated". If no header is exposed, mark provider **unsupported** rather than fabricate. |
### 3. Z.ai / Zhipu GLM (Coding Plan) — **REACHABLE (MEDIUM-HIGH)**
| Item | Value |
|---|---|
| **Cred file** | API key is the model — Z.ai Coding Plan is **API-key based** (not OAuth). Users paste the key into their tool's config (e.g. Claude Code `settings.json`, Cline, `.env`). Env vars: `ZAI_API_KEY` / `ZHIPU_API_KEY`. No canonical local login session to reuse. |
| **Auth type** | API key (Bearer) |
| **Coding-plan endpoint** | `https://api.z.ai/api/coding/paas/v4` (model inference — **must use this**, not the general `/paas/v4`) |
| **Usage/quota endpoint** | `https://api.z.ai/api/monitor/usage/quota/limit` and `https://api.z.ai/api/monitor/usage/` — **undocumented**, but consumed by working tools (CodexBar, opencode-mystatus, `ai-usagebar`, `pi-usage-bars`). Auth = the API key as Bearer. |
| **Programmatic access verdict** | **YES — MEDIUM-HIGH confidence.** Undocumented but proven by 3+ shipping implementations, including per-provider 5-hour-window and weekly-limit tracking. |
| **Caveat** | Endpoint is undocumented → brittle. Falls back to BigModel (`open.bigmodel.cn`) which serves the same quota data. **Auto-detect is weak here** (no local login file by default) — likely needs the manual API-key fallback path. |
### 4. OpenCode GO (opencode.ai) — **CREDS REACHABLE; USAGE PROXY (MEDIUM)**
| Item | Value |
|---|---|
| **Cred file (Windows)** | `%LOCALAPPDATA%\opencode\auth.json` (primary) and/or `%USERPROFILE%\.local\share\opencode\auth.json` (fallback). Override: `OPENCODE_CONFIG_DIR` env var. Config: `%APPDATA%\opencode\opencode.json`. |
| **Auth type** | OAuth-ish `auth.json` produced by `/connect` → `opencode.ai/auth` browser flow; contains an access token (and refresh) for the **OpenCode** subscription ($5/$10/mo for "popular open coding models"). For BYO-provider keys, OpenCode stores them in its config / env. |
| **Usage endpoint** | OpenCode GO is itself a **reseller/proxy** to upstream models. There is no first-party public usage REST API in the OpenCode docs found. Community projects (opencode-mystatus) query upstream provider quotas rather than an OpenCode-usage endpoint. |
| **Programmatic access verdict** | **MAYBE — MEDIUM confidence.** Credentials are trivially readable (plain JSON, no OS keychain). But OpenCode GO does **not** clearly expose its own per-period quota endpoint; usage likely has to be inferred from the upstream provider the OpenCode plan maps to, or from response headers. |
| **Recommendation** | For v1, read the OpenCode auth token and attempt a status/usage probe against `opencode.ai`; if none exists, display OpenCode as "logged in (usage N/A)" rather than fake a number. Re-evaluate in a spike. |
### 5. MiniMax (Coding/Token Plan) — **HARDEST; COOKIE-SESSION (MEDIUM-LOW)**
| Item | Value |
|---|---|
| **Cred file** | API key (`Authorization: Bearer ...`) or Subscription Key (`sk-cp-...`) stored in tool config / env — no canonical local login file. Token Plan quota is shown in the platform.minimax.io **Billing > Token Plan** UI as a usage bar. |
| **Auth type** | API key / Subscription Key for inference. |
| **Usage endpoint** | `https://www.minimaxi.com/v1/api/openplatform/coding_plan/remains` — returns `current_interval_usage_count`, `current_weekly_usage_count`. **BUT: per MiniMax-M2#88, it requires a cookie session, not a standard API key.** |
| **Programmatic access verdict** | **HARD / MEDIUM-LOW confidence.** The endpoint exists and returns the right fields, but the cookie-session requirement breaks the clean "reuse stored API key" model. |
| **Recommendation** | Plan a dedicated **spike phase** for MiniMax before committing. Options ranked: (1) capture the session cookie the user already has in their browser (fragile, requires browser cookie extraction — high privacy friction); (2) parse `x-*` rate-limit headers on inference responses (needs the user to actually make calls, not passive polling); (3) mark MiniMax **"unsupported — open console"** in v1 with a deep-link button. Default to (3) unless the spike finds a clean path. |
### Provider access summary table
| Provider | Auto-detect cred file (Windows) | Auth type | Usage reachable? | Confidence | v1 strategy |
|---|---|---|---|---|---|
| **Codex (ChatGPT sub)** | `%USERPROFILE%\.codex\auth.json` | OAuth (id/access/refresh JWT) | **YES** via `chatgpt.com/backend-api/wham/usage` | HIGH | Session-reuse + OAuth refresh; primary feature |
| **OpenAI (API key)** | `.codex/auth.json` `OPENAI_API_KEY` or env | Admin API key `sk-admin-...` | **YES** via `/v1/organization/costs` | HIGH | Show $ spend (not %) |
| **Grok (xAI)** | `%USERPROFILE%\.grok\auth.json` (Grok Build) or `XAI_API_KEY` env | OAuth session OR API key | **MAYBE** — header-derived; no public quota endpoint | MEDIUM | Best-effort from rate-limit headers; "estimated" |
| **Z.ai / Zhipu** | API key in tool config / env (`ZAI_API_KEY`) | API key (Bearer) | **YES** via `api.z.ai/api/monitor/usage/quota/limit` (undocumented) | MEDIUM-HIGH | Manual API-key fallback; undocumented endpoint → brittle |
| **OpenCode GO** | `%LOCALAPPDATA%\opencode\auth.json` | OAuth (access/refresh) | **MAYBE** — no first-party usage API found | MEDIUM | Show "logged in"; usage N/A unless spike finds endpoint |
| **MiniMax** | API/Sub key in tool config / env | API key / Subscription Key | **HARD** — `/coding_plan/remains` needs cookie session | MEDIUM-LOW | Default to "unsupported — open console" unless spike succeeds |
## Session-Reuse vs API-Key — Architectural Implications
| Family | Providers | Stack implication |
|---|---|---|
| **Session-reuse (OAuth)** | Codex (ChatGPT sub), Grok Build, OpenCode GO | `HttpClient` per provider with its **own cookie container**; must (a) **refresh** the short-lived `access_token` via `refresh_token` before polling, (b) send `Authorization: Bearer <access_token>`, (c) handle `401` → refresh → retry-once. **Critical:** refresh sparingly and never concurrently with the user's real CLI session to avoid token rotation invalidating their login. |
| **API-key** | OpenAI (payg), Z.ai, MiniMax(inference), Grok (API) | Simpler: stateless Bearer header. But each provider's key lives in a **different** place (env var, `.codex/auth.json`, tool-specific config JSON). Adapter discovery layer must probe multiple known paths + env vars. |
- **HttpClient factory with named, isolated clients per provider** (one `HttpClient` + one `HttpClientHandler`/`CookieContainer` per provider) — non-negotiable so a Codex cookie never leaks into a Grok call.
- **OAuth refresh is a first-class concern**, not an afterthought. Build a small `TokenManager` that: loads `auth.json` → checks `last_refresh`/token exp → refreshes via the provider's token endpoint → **writes back only if rotation occurred** (and even then, consider read-only mode to avoid clobbering the user's session — see Security below).
- **Header/cookie fidelity matters.** Some endpoints (Codex `wham/usage`, MiniMax `coding_plan/remains`) may expect browser-like headers (`User-Agent`, `Cookie`, sometimes `x-csrf`). The C# `HttpClient` gives full control here; this is a point against webview-only stacks where you'd fight CORS/sandboxing.
- **Admin-key gate for OpenAI**: standard `sk-` keys get 403 on `/v1/organization/costs`; the adapter must detect this and prompt the user to create an `sk-admin-...` key, not silently fail.
## Scheduled Polling Approach (10-minute, conservative)
## Credential Security — Read Tokens Without Leaking Them
### Principles
### Concrete C# sketch
## Installation
# 1. Create the project
# 2. Add supporting packages
# 3. (Dev) run
# 4. Ship a single portable EXE
# -> bin\Release\net8.0-windows\win-x64\publish\PlanMeter.exe  (one file, ~60-80 MB)
## Alternatives Considered
| Recommended | Alternative | When to Use Alternative |
|---|---|---|
| **WPF (.NET 8)** | **WinUI 3 / Windows App SDK 1.6** | Only if you specifically need Fluent/WinUI controls or MSIX Store auto-update. Penalized here by no single-file EXE and an active topmost-window bug ([microsoft-ui-xaml#9990](https://github.com/microsoft/microsoft-ui-xaml/issues/9990)). |
| **WPF (.NET 8)** | **Tauri 2.x (Rust + WebView2)** | If the maintainer is stronger in Rust/frontend than C#, **and** wants a ~5–10 MB installer. Pays for cross-platform reach the project doesn't need; WebView2 RAM on Windows negates the "lighter" claim ([tauri#5889](https://github.com/tauri-apps/tauri/issues/5889)). |
| **WPF (.NET 8)** | **PySide6 / PyQt6** | If Python is the only language available. Lowest RAM (20–50 MB), excellent `QSystemTrayIcon` + `WindowStaysOnTopHint`, but: bundling a Python app as a clean EXE (PyInstaller/Nuitka) is more fragile than `dotnet publish`, and the OAuth/DPAPI/HTTP ergonomics are weaker than C#. Viable runner-up to the runner-up. |
| **WPF (.NET 8)** | **Electron** | Never for an always-on widget. ~150–500 MB idle RAM is unacceptable. |
| **WPF (.NET 8)** | **Pure Win32 / WinForms** | WinForms is fine but dated for a polished floating widget; pure Win32 is overkill. WPF gives borderless/transparent/animated for free. |
| **`PeriodicTimer` + hosted service** | **OS Task Scheduler** | Overkill — the app is already resident; adds install friction and a second process. |
## What NOT to Use
| Avoid | Why | Use Instead |
|---|---|---|
| **Electron** | ~150–500 MB idle RAM for a tiny always-on widget; bundles a full Chromium. Directly violates the "low-footprint widget" requirement. | WPF (.NET 8) |
| **WinUI 3 / Windows App SDK 1.6** | (1) Cannot produce a true single-file EXE (native runtime dependency). (2) Active bug strips `TOPMOST` from windows of *other* processes on launch — fatal for a topmost widget. | WPF (.NET 8) |
| **`DispatcherTimer`/`Forms.Timer` for polling** | Runs on the UI thread; a hung provider call freezes the widget UI. | `PeriodicTimer` inside `IHostedService` |
| **Aggressive Polly retries (3+)** | Session-reuse providers (Codex, OpenCode) will flag repeated failed auth/usage calls as abuse → risk-control / session invalidation. PROJECT.md explicitly calls this out. | ≤1 retry + backoff; degrade UI on failure |
| **Plaintext storage of manual API keys** | At-rest secret in user-writable file = trivial theft. | DPAPI (`ProtectedData`, `CurrentUser` scope) |
| **Any telemetry / crash-reporting SDK** | A credential-handling personal utility must have zero out-of-process data flow. One phone-home and the tool is untrustworthy. | Allow-listed egress only; local file logs only |
| **Mutating the upstream tool's `auth.json`** | Refreshing the user's Codex/OpenCode OAuth token can rotate the refresh token and **invalidate their actual CLI session** (documented hazard). | Read-only on source files; cache refreshed tokens in app's own DPAPI store |
| **Old OpenAI `/v1/dashboard/billing/usage`** | **Deprecated/removed** — returns nothing. | `/v1/organization/costs` + `/v1/organization/usage/completions` with an `sk-admin-` key |
| **Standard `sk-` OpenAI key for usage** | Gets 403 on Costs/Usage endpoints. | Org Admin key (`sk-admin-...`) |
| **Z.ai general endpoint `/api/paas/v4` for coding-plan usage** | Wrong base URL → auth/model errors; coding plan requires `/api/coding/paas/v4` for inference and `/api/monitor/usage/quota/limit` for quota. | Use the coding-plan-specific paths |
| **`npx --yes ctx7`** (per lookup ref) | Silently executes unverified registry packages. | Documented WebSearch/WebFetch fallback, used here |
## Stack Patterns by Variant
- Fall through to the **manual API-key entry** path (PROJECT.md Active requirement). Store via DPAPI. Adapter uses stateless Bearer auth. This is the expected path for these three.
- Treat the OAuth `access_token` as expired; attempt **one** refresh via `refresh_token`; on second failure, mark provider "re-login needed" and **stop polling it** for the session (do not retry into risk-control).
- Derive a coarse "remaining %" from `x-ratelimit-remaining` / `x-ratelimit-limit` on a cheap probe call (e.g. `GET /v1/models`). Display as "estimated" and poll at the same 10-min cadence — do not invent a higher-cadence header-scraper.
- **Spike first.** If no clean non-cookie path exists, ship MiniMax as "unsupported — open console" with a deep link to `platform.minimax.io/subscribe/token-plan`. Do not implement fragile browser-cookie extraction in v1 — the privacy/security cost outweighs one provider's coverage.
- The chosen WPF stack does not port. That's the deliberate trade-off of "Windows-only for v1" (PROJECT.md). A future cross-platform v2 would rewrite the UI shell in Tauri 2 while **reusing the C# provider-adapter contract** if it's kept behind a clean interface — or port adapters to Rust. Plan for adapter-portability now by keeping provider logic out of the WPF layer.
## Version Compatibility
| Package A | Compatible With | Notes |
|---|---|---|
| `net8.0-windows` (WPF) | Windows 10 1607+ / Windows 11 | WPF on .NET 8 requires Win10 1607+; matches PROJECT.md "Windows 10/11 only". |
| `Microsoft.Extensions.Hosting` 8.x | .NET 8 | Pin major to 8 to avoid preview-9 churn. |
| `Polly` 8.x | .NET 8 | v8 API (`ResiliencePipeline`) differs from v7 — don't copy v7 snippets. |
| `System.Security.Cryptography.ProtectedData` | Windows only | Intentionally Windows-only; consistent with scope. |
| Tauri 2.x (if chosen as runner-up) | WebView2 Runtime ≥ 125.0.2535.41 on target machine; preinstalled on Win11, bootstrapped on Win10 | Embed fixed WebView2 only if targeting air-gapped machines (adds ~180 MB). |
## Sources
### Framework comparison / WPF vs WinUI 3 vs Tauri vs Electron vs PySide6
- Microsoft Learn — Manage app windows (WinUI 3 always-on-top + known topmost limitations): https://learn.microsoft.com/en-us/windows/apps/develop/ui/manage-app-windows — HIGH
- microsoft-ui-xaml#9990 (WinUI 3 topmost regression in WAS 1.6): https://github.com/microsoft/microsoft-ui-xaml/issues/9990 — HIGH
- Microsoft Learn — self-contained deploy (WinUI 3 cannot single-file): https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps — HIGH
- Tauri vs Electron benchmark (~58% less RAM, ~96% smaller bundle; Windows delta negligible): https://www.reddit.com/r/programming/comments/1jwjw7b/tauri_vs_electron_benchmark_58_less_memory_96/ — MEDIUM
- Tauri can exceed Electron RAM on Windows (tauri#5889): https://github.com/tauri-apps/tauri/issues/5889 — HIGH
- Qt vs Electron vs Tauri 2025 (PySide6 20–50 MB, low RAM): https://softwarelogic.co/en/blog/migration-secrets-choosing-qt-electron-or-tauri-for-desktop-apps-2025 — MEDIUM
- Tauri 2 stable always-on-top config (`alwaysOnTop`): https://v2.tauri.app/reference/config/ — HIGH
- Tauri Windows installer (MSI/NSIS) + fixed WebView2 (~180 MB): https://v2.tauri.app/distribute/windows-installer/ — HIGH
### OpenAI Codex (ChatGPT subscription + API key)
- ChatGPT Learn — Codex Auth (two paths, `auth.json`): https://learn.chatgpt.com/docs/auth — HIGH
- Codex CLI config location `%USERPROFILE%\.codex\auth.json`: https://inventivehq.com/knowledge-base/openai/where-configuration-files-are-stored — MEDIUM
- openai/codex#10869 — Codex CLI polls `chatgpt.com/backend-api/wham/usage`: https://github.com/openai/codex/issues/10869 — HIGH (vendor's own client)
- Simon Willison — Codex private API `chatgpt.com/backend-api/codex/responses` + OAuth bearer: https://simonwillison.net/2025/Nov/9/gpt-5-codex-mini/ — HIGH
- LangChain `_ChatOpenAICodex` OAuth backend reference: https://reference.langchain.com/python/langchain-openai/chat-models/codex — MEDIUM
- OpenAI Help — Using Codex with your ChatGPT plan (5h rolling + weekly cap, no public quota REST API): https://help.openai.com/en/articles/11369540/using-codex-with-your-chatgpt-plan — HIGH
- OpenAI Costs API reference (`GET /organization/costs`, Admin key required): https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/costs/ — HIGH
- OpenAI Cookbook — Usage/Cost APIs + Admin Key setup: https://developers.openai.com/cookbook/examples/completions_usage_api — HIGH
- ManageEngine/SaaS Manager — standard `sk-` rejected, `sk-admin-` required: https://www.manageengine.com/eu/saas-management/help/openai.html — MEDIUM
- Community — `/v1/dashboard/billing/usage` deprecated/removed: https://community.openai.com/t/v1-dashboard-billing-usage-is-not-work/305887 — MEDIUM
### xAI Grok
- xAI Quickstart (`XAI_API_KEY`, `https://api.x.ai/v1`, OpenAI/Anthropic SDK compatible): https://docs.x.ai/developers/quickstart — HIGH
- xAI Console (keys + billing/usage UI): https://console.x.ai/ — HIGH
- Grok Build CLI auth (`~/.grok/auth.json`, refreshable): https://docs.x.ai/build/overview — MEDIUM-HIGH
### Z.ai / Zhipu
- Z.ai Quick Start (API Keys page, Billing page): https://docs.z.ai/guides/overview/quick-start — HIGH
- Z.ai FAQ — coding-plan endpoint `https://api.z.ai/api/coding/paas/v4`: https://docs.z.ai/devpack/faq — HIGH
- ZCode config — "do not replace coding endpoint with general `/paas/v4`": https://zcode.z.ai/en/docs/configuration — MEDIUM-HIGH
- CodexBar / opencode-mystatus / ai-usagebar consume `api.z.ai/api/monitor/usage/quota/limit` (undocumented): https://github.com/steipete/CodexBar , https://github.com/vbgate/opencode-mystatus — MEDIUM (multi-source working implementations)
### OpenCode GO (opencode.ai)
- OpenCode Config docs: https://opencode.ai/docs/config/ — HIGH
- OpenCode Troubleshooting (Windows `%APPDATA%` search hint): https://opencode.ai/docs/troubleshooting/ — MEDIUM-HIGH
- griffinmartin/opencode-claude-auth — Windows paths `%LOCALAPPDATA%\opencode\auth.json` + `%USERPROFILE%\.local\share\opencode\auth.json`: https://github.com/griffinmartin/opencode-claude-auth — MEDIUM
- OpenCode Go subscription ($5/$10 mo): https://opencode.ai/docs/go/ — HIGH
### MiniMax
- MiniMax Token Plan intro / FAQ (usage bar, Subscription Key): https://platform.minimax.io/docs/token-plan/intro , https://platform.minimax.io/docs/token-plan/faq — HIGH
- MiniMax-M2#88 — `/coding_plan/remains` requires **cookie session**: https://github.com/MiniMax-AI/MiniMax-M2/issues/88 — MEDIUM (single issue thread; the project's biggest flagged risk)
- MiniMax-M2#99 — `coding_plan/remains` returns `current_interval_usage_count` / `current_weekly_usage_count`: https://github.com/MiniMax-AI/MiniMax-M2/issues/99 — MEDIUM
### Cross-validation: existing multi-provider quota tools (proves feasibility)
- steipete/CodexBar — 67+ provider IDs, incl. OpenAI Codex, z.ai, Grok; macOS menu bar (proves the quota data is reachable): https://github.com/steipete/CodexBar — HIGH (existence proof)
- vbgate/opencode-mystatus — supports OpenAI, Zhipu AI, Google Antigravity: https://github.com/vbgate/opencode-mystatus — MEDIUM-HIGH
<!-- GSD:stack-end -->

<!-- GSD:conventions-start source:CONVENTIONS.md -->
## Conventions

Conventions not yet established. Will populate as patterns emerge during development.
<!-- GSD:conventions-end -->

<!-- GSD:architecture-start source:ARCHITECTURE.md -->
## Architecture

Architecture not yet mapped. Follow existing patterns found in the codebase.
<!-- GSD:architecture-end -->

<!-- GSD:skills-start source:skills/ -->
## Project Skills

No project skills found. Add skills to any of: `.claude/skills/`, `.agents/skills/`, `.cursor/skills/`, `.github/skills/`, or `.codex/skills/` with a `SKILL.md` index file.
<!-- GSD:skills-end -->

<!-- GSD:workflow-start source:GSD defaults -->
## GSD Workflow Enforcement

Before using Edit, Write, or other file-changing tools, start work through a GSD command so planning artifacts and execution context stay in sync.

Use these entry points:
- `/gsd-quick` for small fixes, doc updates, and ad-hoc tasks
- `/gsd-debug` for investigation and bug fixing
- `/gsd-execute-phase` for planned phase work

Do not make direct repo edits outside a GSD workflow unless the user explicitly asks to bypass it.
<!-- GSD:workflow-end -->



<!-- GSD:profile-start -->
## Developer Profile

> Profile not yet configured. Run `/gsd-profile-user` to generate your developer profile.
> This section is managed by `generate-claude-profile` -- do not edit manually.
<!-- GSD:profile-end -->
