using System;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Extensions.Http;

namespace PlanMeter.Core.Http;

/// <summary>
/// DI wiring for the named "zai" HttpClient. The handler chain order is load-bearing:
/// AllowListHandler (OUTERMOST — refuses before send) → RedactingHandler → Polly
/// transient-error policy (≤1 retry, short backoff) → network.
/// </summary>
public static class HttpExtensions
{
    /// <summary>
    /// The Z.ai quota endpoint. SEC-02 allow-list set for Phase 1 is the single host
    /// <c>api.z.ai</c>; Phase 4/5 expands this centrally (never per-adapter).
    /// </summary>
    public const string ZaiClientName = "zai";

    public const string ZaiBaseUrl = "https://api.z.ai/";
    public const string ZaiQuotaLimitPath = "api/monitor/usage/quota/limit";

    /// <summary>
    /// Default Z.ai HttpClient timeout. Short — STACK.md "What NOT to Use": ≤1 retry,
    /// short timeout; a failed poll degrades the UI, never retry-storms.
    /// </summary>
    public static readonly TimeSpan ZaiTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The MiniMax token-plan endpoint constants. Spike-confirmed (D-01/D-02, BRANCH A):
    /// CN host api.minimaxi.com answered with key-based auth at /v1/token_plan/remains.
    /// </summary>
    public const string MinimaxClientName = "minimax";

    public const string MinimaxBaseUrl = "https://api.minimaxi.com/";
    public const string MinimaxTokenPlanRemainsPath = "v1/token_plan/remains";

    /// <summary>
    /// Default MiniMax HttpClient timeout — same conservative 15s as Z.ai.
    /// </summary>
    public static readonly TimeSpan MinimaxTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The OpenCode GO usage endpoint constants. Live-verified 2026-08-19:
    /// GET https://opencode.ai/zen/go/v1/usage with Bearer API key.
    /// </summary>
    public const string OpenCodeClientName = "opencode";

    public const string OpenCodeBaseUrl = "https://opencode.ai/";
    public const string OpenCodeUsagePath = "zen/go/v1/usage";

    /// <summary>Default OpenCode GO HttpClient timeout — same conservative 15s as Z.ai.</summary>
    public static readonly TimeSpan OpenCodeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The Codex on-demand usage endpoint constants. GET chatgpt.com/backend-api/wham/usage
    /// with the local Codex CLI session token. Host is already on <see cref="ProviderAllowedHosts"/>.
    /// </summary>
    public const string CodexClientName = "codex";

    public const string CodexBaseUrl = "https://chatgpt.com/";
    public const string CodexUsagePath = "backend-api/wham/usage";

    /// <summary>Default Codex HttpClient timeout — same conservative 15s as Z.ai.</summary>
    public static readonly TimeSpan CodexTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The Grok OAuth device-flow endpoints (GROK-03). Host <c>auth.x.ai</c> was pinned
    /// onto <see cref="ProviderAllowedHosts"/> in Phase 7 — this block must NOT grow the
    /// array (GRND-01).
    /// </summary>
    public const string GrokAuthClientName = "grok-auth";

    public const string GrokAuthBaseUrl = "https://auth.x.ai/";

    /// <summary>Default grok-auth HttpClient timeout — same conservative 15s as Z.ai.</summary>
    public static readonly TimeSpan GrokAuthTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The Grok billing gRPC-web endpoint constants (GROK-02). Host <c>grok.com</c> was
    /// pinned onto <see cref="ProviderAllowedHosts"/> in Phase 7 — the array stays at six
    /// hosts. The path is the reference-verified connect-RPC method
    /// (<c>package.Service/Method</c>); requests POST an empty 5-byte gRPC-web frame
    /// with browser-fidelity headers (built in 10-02's adapter / 10-01's live tracer).
    /// </summary>
    public const string GrokBillingClientName = "grok-billing";

    public const string GrokBillingBaseUrl = "https://grok.com/";

    public const string GrokBillingPath = "grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig";

    /// <summary>Default grok-billing HttpClient timeout — same conservative 15s as Z.ai.</summary>
    public static readonly TimeSpan GrokBillingTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Phase 1 allow-list. Single host — exact match only. Renamed (04-02) to
    /// <see cref="ProviderAllowedHosts"/> as the centralized array per-provider plans
    /// append their hosts to; the Z.ai alias keeps existing references compiling.
    /// </summary>
    public static readonly string[] ZaiAllowedHosts = { "api.z.ai" };

    /// <summary>
    /// 04-02/D-13 — THE centralized provider-host allow-list (SEC-02). Per-provider
    /// plans grow THIS array (api.minimaxi.com if MiniMax ships, api.x.ai if Grok
    /// ships, opencode.ai for the live OpenCode GO usage client) — never a per-adapter
    /// set, never a second AllowListOptions binding. <see cref="AddPlanMeterProviderClient"/>
    /// validates every requested host against this array at registration time.
    /// </summary>
    public static readonly string[] ProviderAllowedHosts = { "api.z.ai", "api.minimaxi.com", "chatgpt.com", "auth.x.ai", "grok.com", "opencode.ai" };

    /// <summary>
    /// Upper bound on the response header read. SEC-02 defence-in-depth: a hostile or
    /// compromised upstream could otherwise stream an unbounded header block to exhaust
    /// memory. 16 KiB is well above any legitimate Z.ai response header block.
    /// </summary>
    public const int MaxResponseHeadersLength = 16384;

    /// <summary>
    /// Registers the named "zai" HttpClient with the SEC-02 allow-list, SEC-03 redactor,
    /// and the conservative Polly retry policy. Call once from the Generic Host bootstrap
    /// (App.xaml.cs). 04-02: delegates to <see cref="AddPlanMeterProviderClient"/> —
    /// zero behavior change (the shared chain lives in exactly one place).
    /// </summary>
    public static IServiceCollection AddPlanMeterZaiClient(this IServiceCollection services)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        return AddPlanMeterProviderClient(services, ZaiClientName, ZaiBaseUrl, ZaiTimeout, "api.z.ai");
    }

    /// <summary>
    /// Registers the named "minimax" HttpClient with the SEC-02 allow-list, SEC-03 redactor,
    /// and the conservative Polly retry policy.
    /// </summary>
    public static IServiceCollection AddPlanMeterMinimaxClient(this IServiceCollection services)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        return AddPlanMeterProviderClient(services, MinimaxClientName, MinimaxBaseUrl, MinimaxTimeout, "api.minimaxi.com");
    }

    /// <summary>
    /// Registers the named "opencode" HttpClient with the SEC-02 allow-list, SEC-03 redactor,
    /// and the conservative Polly retry policy.
    /// </summary>
    public static IServiceCollection AddPlanMeterOpenCodeClient(this IServiceCollection services)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        return AddPlanMeterProviderClient(services, OpenCodeClientName, OpenCodeBaseUrl, OpenCodeTimeout, "opencode.ai");
    }

    /// <summary>
    /// Registers the named "codex" HttpClient with the SEC-02 allow-list, SEC-03 redactor,
    /// and the conservative Polly retry policy. Host is <c>chatgpt.com</c> (already in the
    /// six-host set) — this method must not grow <see cref="ProviderAllowedHosts"/>.
    /// </summary>
    public static IServiceCollection AddPlanMeterCodexClient(this IServiceCollection services)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        return AddPlanMeterProviderClient(services, CodexClientName, CodexBaseUrl, CodexTimeout, "chatgpt.com");
    }

    /// <summary>
    /// Registers the named "grok-auth" HttpClient (GROK-03) with the SEC-02 allow-list,
    /// SEC-03 redactor, and the conservative Polly retry policy. Host is <c>auth.x.ai</c>
    /// (already in the six-host set) — this method must not grow
    /// <see cref="ProviderAllowedHosts"/>.
    /// </summary>
    public static IServiceCollection AddPlanMeterGrokAuthClient(this IServiceCollection services)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        return AddPlanMeterProviderClient(services, GrokAuthClientName, GrokAuthBaseUrl, GrokAuthTimeout, "auth.x.ai");
    }

    /// <summary>
    /// Registers the named "grok-billing" HttpClient (GROK-02) with the SEC-02 allow-list,
    /// SEC-03 redactor, and the conservative Polly retry policy. Host is <c>grok.com</c>
    /// (already in the six-host set) — this method must not grow
    /// <see cref="ProviderAllowedHosts"/>.
    /// </summary>
    public static IServiceCollection AddPlanMeterGrokBillingClient(this IServiceCollection services)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        return AddPlanMeterProviderClient(services, GrokBillingClientName, GrokBillingBaseUrl, GrokBillingTimeout, "grok.com");
    }

    /// <summary>
    /// 04-02/D-13 — the Phase-2-DEFERRED generalized named-client helper. Registers a
    /// named provider HttpClient with the EXACT handler chain of the Phase-1 zai client:
    /// SocketsHttpHandler (AllowAutoRedirect=false, UseCookies=false,
    /// MaxResponseHeadersLength bound) → AllowListHandler (OUTERMOST, SEC-02) →
    /// RedactingHandler (SEC-03) → GetRetryPolicy() (Polly ≤1 transient retry, 500ms).
    ///
    /// HOST VALIDATION (T-04-05): every requested host MUST already be present in the
    /// centralized <see cref="ProviderAllowedHosts"/> array — unknown hosts throw
    /// <see cref="ArgumentException"/> at REGISTRATION time. The allow-list array stays
    /// the single source of egress truth, grown only by an explicit central edit; the
    /// helper refuses to broaden it implicitly.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="clientName">The named client's key (the adapter's <c>CreateClient</c> name).</param>
    /// <param name="baseUrl">The provider base URL (its host must be on the centralized allow-list).</param>
    /// <param name="timeout">The client timeout — short (STACK.md "What NOT to Use").</param>
    /// <param name="hosts">The hosts this client will talk to; each must be in <see cref="ProviderAllowedHosts"/>.</param>
    public static IServiceCollection AddPlanMeterProviderClient(
        this IServiceCollection services,
        string clientName,
        string baseUrl,
        TimeSpan timeout,
        params string[] hosts)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        if (string.IsNullOrWhiteSpace(clientName))
        {
            throw new ArgumentException("Client name must be non-empty.", nameof(clientName));
        }

        if (hosts is null || hosts.Length == 0)
        {
            throw new ArgumentException(
                "At least one host must be declared — a provider client without hosts has no egress path.",
                nameof(hosts));
        }

        // T-04-05 — validate every requested host against the CENTRALIZED array. This is
        // the registration-time gate that keeps a per-provider plan from silently
        // broadening egress: the plan must first add its host to ProviderAllowedHosts
        // (a reviewed central edit), then pass it here.
        foreach (string host in hosts)
        {
            if (Array.IndexOf(ProviderAllowedHosts, host) < 0)
            {
                throw new ArgumentException(
                    $"Host '{host}' is not in the centralized ProviderAllowedHosts array — add it there first (the allow-list is the single source of egress truth, never broadened implicitly).",
                    nameof(hosts));
            }
        }

        // Bind the allow-list options singleton from the centralized array — ONE
        // binding (TryAdd semantics via Contains-check on the service descriptor so a
        // second helper call never registers a second AllowListOptions).
        if (!services.Any(d => d.ServiceType == typeof(AllowListOptions)))
        {
            services.AddSingleton(_ => new AllowListOptions
            {
                AllowedHosts = new System.Collections.Generic.HashSet<string>(
                    ProviderAllowedHosts,
                    StringComparer.OrdinalIgnoreCase),
            });
        }

        // Register handlers as transient — DelegatingHandlers are not reused across
        // requests by IHttpClientFactory's pooling; each request gets a fresh chain.
        services.AddTransient<AllowListHandler>();
        services.AddTransient<RedactingHandler>();

        services.AddHttpClient(clientName, client =>
        {
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = timeout;
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        })
        // SEC-02/CR-02 — pin the primary handler and DISABLE auto-redirect. The framework
        // default (SocketsHttpHandler / HttpClientHandler) follows 3xx responses
        // transparently, and the follow-up request to whatever host the Location header
        // names does NOT pass through AllowListHandler — so a 302 from an allowed host
        // (intentional, via compromise, or via a transparent proxy) could route a
        // follow-up to an attacker host past the allow-list. AllowAutoRedirect=false makes
        // the 3xx surface as the response (which the adapter then classifies as Error).
        // UseCookies=false: no cookie jar is needed (Bearer auth), and disabling cookies
        // prevents any cross-call cookie accumulation from being silently forwarded by a
        // future redirect. MaxResponseHeadersLength bounds the header read (defence in
        // depth against header-bomb).
        .ConfigurePrimaryHttpMessageHandler(() =>
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                MaxResponseHeadersLength = MaxResponseHeadersLength,
            })
        // OUTERMOST — refuse non-allow-listed hosts before SendAsync. The refusal
        // happens before RedactingHandler or the network sees the request (SEC-02/ordering).
        .AddHttpMessageHandler<AllowListHandler>()
        // SEC-03 — strip Authorization / Cookie / x-api-key + token-shape from any
        // representation a downstream logger sees.
        .AddHttpMessageHandler<RedactingHandler>()
        // Polly CONSERVATIVE — STACK.md "What NOT to Use": ≤1 retry + short backoff.
        // A failed poll degrades the UI (STALE/ERROR), never retry-storms.
        .AddPolicyHandler(GetRetryPolicy());

        return services;
    }

    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    {
        // Transient errors only — HttpPolicyExtensions.HandleTransientHttpError() already
        // covers HTTP 408 (RequestTimeout), 5xx, and HttpRequestException. NO retry on
        // 401/403/429. Wait 500ms once; that is the entire retry budget.
        // WR-08: the prior `.OrResult(msg => msg.StatusCode == HttpStatusCode.RequestTimeout)`
        // was a no-op — 408 was matched twice. A future reader would have assumed 408 was
        // special-cased beyond HandleTransientHttpError's coverage, which it is not.
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .WaitAndRetryAsync(retryCount: 1, _ => TimeSpan.FromMilliseconds(500));
    }
}
