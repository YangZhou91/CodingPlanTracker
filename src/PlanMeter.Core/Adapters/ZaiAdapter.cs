using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PlanMeter.Core.Credentials;
using PlanMeter.Core.Http;
using PlanMeter.Core.Models;

namespace PlanMeter.Core.Adapters;

/// <summary>
/// ZAI-01 — the reference adapter. Issues <c>GET https://api.z.ai/api/monitor/usage/quota/limit</c>
/// through the named "zai" <see cref="HttpClient"/> so the SEC-02 allow-list +
/// SEC-03 redactor handlers run on every call. Never self-refreshes or mints a token
/// on 401 (SEC-01 prohibition #2).
/// </summary>
/// <remarks>
/// Phase 2 (D-11): implements <see cref="IProviderAdapter"/> with ZERO behavior change —
/// the class doc's "Phase 2 extraction is mechanical" promise. The id is "zai" (the
/// store/registry/poller key); <see cref="DisplayName"/> is "Z.ai" (the row + the
/// <c>UsageReading.Provider</c> field). <see cref="FetchUsageAsync"/> now returns
/// <see cref="AdapterFetchResult"/> so the poller can read the 429 <c>Retry-After</c>
/// for REFRESH-03 cross-tick backoff (RESEARCH §Normalizer verbatim — UsageReading
/// gains no fields).
///
/// Test seam: <see cref="FetchCount"/> records the number of <see cref="FetchUsageAsync"/>
/// invocations so the R3 funneling test can assert "exactly one fetch from a double-click".
/// </remarks>
public sealed class ZaiAdapter : IProviderAdapter
{
    private readonly IHttpClientFactory _factory;
    private int _fetchCount;

    public ZaiAdapter(IHttpClientFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>
    /// The machine identity — the key of the store / registry / poller slot (D-13).
    /// </summary>
    public ProviderId Id => "zai";

    /// <summary>Human-readable provider name for the row and the <c>UsageReading.Provider</c> field.</summary>
    public string DisplayName => "Z.ai";

    /// <summary>Z.ai exposes a programmatic usage endpoint, so usage is fetchable.</summary>
    public bool SupportsUsageApi => true;

    /// <summary>
    /// D-10 — Z.ai is API-key based (no local login session to reuse); the settings
    /// window renders a key field for this adapter.
    /// </summary>
    public bool RequiresManualKey => true;

    /// <summary>GRND-02 — Z.ai polls on schedule, not manual-only.</summary>
    public bool ManualOnlyFetch => false;

    /// <summary>GRND-02/D-07 — Z.ai uses a manual API key, not OAuth login.</summary>
    public bool SupportsOAuthLogin => false;

    /// <summary>Phase-4 groundwork — Z.ai authenticates with a manual API key.</summary>
    public AuthFamily AuthFamily => AuthFamily.Key;

    /// <summary>D-09 — the Z.ai console deep-link (the RE-LOGIN tooltip hyperlink).</summary>
    public string? ConsoleUrl => "https://z.ai/";

    /// <summary>Z.ai's figure is an exact reading — no qualifier badge.</summary>
    public string? QualifierText => null;

    /// <summary>Z.ai supports usage polling — no Unsupported verdict tooltip.</summary>
    public string? UnsupportedReason => null;

    /// <summary>Z.ai is not a floor row — no floor tooltip.</summary>
    public string? FloorReason => null;

    /// <summary>
    /// D-16 — Z.ai is KEY-family with ConsoleUrl already parameterized; the existing
    /// BuildReLoginTooltip uses Adapter.DisplayName and Adapter.ConsoleUrl. Null = keep
    /// existing parameterized text.
    /// </summary>
    public string? ReLoginGuidance => null;

    /// <summary>
    /// PROV-03 presence probe (read-only — SEC-01): the Z.ai manual-key model is a
    /// DPAPI blob; Detect() is blob existence. The blob path is resolved through
    /// <see cref="DpapiKeyStore.ForProvider"/> — the one source of truth that reproduces
    /// the frozen <c>zai.key.bin</c> path exactly (D-07). Never writes or mutates the
    /// credential path.
    /// </summary>
    public bool Detect() => File.Exists(DpapiKeyStore.ForProvider(Id).BlobPath);

    /// <summary>
    /// The number of fetch invocations that have actually hit the named "zai" HttpClient
    /// (across both TestFetchAsync and FetchUsageAsync). Test seam for R3 funneling.
    /// </summary>
    public int FetchCount => Volatile.Read(ref _fetchCount);

    /// <summary>
    /// D-04 — test-fetch BEFORE persisting the key. Called by the Save-key handler
    /// with a freshly-typed candidate key. On 401, the caller MUST NOT store the key.
    /// </summary>
    public async Task<UsageReading> TestFetchAsync(string? apiKey, CancellationToken ct = default)
        => (await FetchCoreAsync(apiKey, ct).ConfigureAwait(false)).Reading;

    /// <summary>
    /// The poller's fetch path — returns the classified reading plus the optional 429
    /// <c>Retry-After</c> carrier (<see cref="AdapterFetchResult"/>).
    /// </summary>
    public Task<AdapterFetchResult> FetchUsageAsync(string? apiKey, CancellationToken ct = default)
        => FetchCoreAsync(apiKey, ct);

    private async Task<AdapterFetchResult> FetchCoreAsync(string? apiKey, CancellationToken ct)
    {
        Interlocked.Increment(ref _fetchCount);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new AdapterFetchResult(new UsageReading(
                Provider: "Z.ai",
                FetchedAtUtc: DateTimeOffset.UtcNow,
                Status: ReadingStatus.NotLoggedIn,
                UsedPct: null,
                RemainingPct: null,
                MostBindingWindow: default,
                AllWindows: null,
                ErrorMessage: "Z.ai rejected the key."), null);
        }

        HttpClient http = _factory.CreateClient(HttpExtensions.ZaiClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, HttpExtensions.ZaiQuotaLimitPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new AdapterFetchResult(ErrorReading("Couldn't reach Z.ai.", ex), null);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Timeout (the named client's 15s) — degrade to Error, never retry-storm.
            return new AdapterFetchResult(ErrorReading("Couldn't reach Z.ai.", ex), null);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // ZAI-01/401 truth: surface NotLoggedIn (RE-LOGIN) and stop. NEVER
                // self-refresh / rotate / mint a token (SEC-01 prohibition #2).
                return new AdapterFetchResult(new UsageReading(
                    Provider: "Z.ai",
                    FetchedAtUtc: DateTimeOffset.UtcNow,
                    Status: ReadingStatus.NotLoggedIn,
                    UsedPct: null,
                    RemainingPct: null,
                    MostBindingWindow: default,
                    AllWindows: null,
                    ErrorMessage: "Z.ai rejected the key."), null);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                // Treat 403 the same as 401 for v1 — both surface RE-LOGIN (SEC-01:
                // never retry into risk-control).
                return new AdapterFetchResult(new UsageReading(
                    Provider: "Z.ai",
                    FetchedAtUtc: DateTimeOffset.UtcNow,
                    Status: ReadingStatus.NotLoggedIn,
                    UsedPct: null,
                    RemainingPct: null,
                    MostBindingWindow: default,
                    AllWindows: null,
                    ErrorMessage: "Z.ai rejected the key."), null);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // D-21 — 429: read the Retry-After into the result so the POLLER can
                // extend its NEXT tick (REFRESH-03 cross-tick backoff). No in-tick retry
                // storm here — Polly stays ≤1 transient retry with no 429 retry. The
                // reading is the same Error the generic non-success branch produces;
                // only the RetryAfter carrier differs.
                TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } when
                        ? when - DateTimeOffset.UtcNow
                        : null);
                return new AdapterFetchResult(ErrorReading("Z.ai is unavailable right now.", null), retryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                // 5xx after Polly's ≤1 retry → Error.
                return new AdapterFetchResult(ErrorReading("Z.ai is unavailable right now.", null), null);
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                return new AdapterFetchResult(ErrorReading("Couldn't read Z.ai's response.", ex), null);
            }

            // ZAI-01 200-on-bad-key trap: Z.ai returns HTTP 200 even for an invalid key.
            // The real reject signal lives in the JSON body envelope — observed codes:
            //   code:401  -> "token expired or incorrect"  (bad/expired bearer token)
            //   code:1001 -> "Authentication parameter not received in Header, unable to authenticate"
            // Pre-fix, this branch fell straight through to ZaiNormalizer, which saw
            // empty data.limits and returned a Q1 Ok-with-null-UsedPct reading — so the
            // invalid key was indistinguishable from a valid key with no usage yet, AND
            // was persisted to the DPAPI blob by SaveKeyAsync's Ok branch. Parse the
            // envelope ONCE here, classify failure envelopes before normalizing, then
            // hand the already-parsed envelope to the ZaiEnvelope overload of Normalize
            // (parse-once — the string overload would re-Deserialize).
            DateTimeOffset fetchedAt = DateTimeOffset.UtcNow;

            ZaiEnvelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<ZaiEnvelope>(body)
                    ?? new ZaiEnvelope();
            }
            catch (JsonException ex)
            {
                return new AdapterFetchResult(ErrorReading("Z.ai sent a malformed response.", ex), null);
            }

            // Discriminator: ZaiNormalizer treats empty data.limits as Q1-Ok. A live
            // success:false envelope would abuse that to look like Q1. Gate the happy
            // path on the envelope's success flag. The `|| Code == 200` widening keeps
            // bodies that carry code:200 but no explicit success field (none observed
            // in live Z.ai traffic — every fixture carries success:true — but defensive)
            // on the Ok path. Q1 empty-data fixture has success:true so it stays Ok.
            if (!(envelope.Success || envelope.Code == 200))
            {
                // Auth-failure codes -> NotLoggedIn (same shape + message as the literal
                // HTTP-401 branch above). SaveKeyAsync's NotLoggedIn case shows the
                // inline error and does NOT call _keyStore.Protect (D-04 intent holds at
                // the semantic level, not just the literal-401 level). Any other failure
                // code -> Error (defensive — never silently Ok an unrecognized shape).
                if (envelope.Code == 401 || envelope.Code == 1001)
                {
                    return new AdapterFetchResult(new UsageReading(
                        Provider: "Z.ai",
                        FetchedAtUtc: fetchedAt,
                        Status: ReadingStatus.NotLoggedIn,
                        UsedPct: null,
                        RemainingPct: null,
                        MostBindingWindow: default,
                        AllWindows: null,
                        ErrorMessage: "Z.ai rejected the key."), null);
                }

                return new AdapterFetchResult(ErrorReading("Z.ai rejected the request.", null), null);
            }

            return new AdapterFetchResult(ZaiNormalizer.Normalize(envelope, fetchedAt), null);
        }
    }

    private static UsageReading ErrorReading(string message, Exception? inner)
    {
        // SEC-03: defensive — never let a raw exception message reach a logger. The
        // RedactingHandler on the named "zai" client already strips token-shape; we
        // do the same here for any inner exception text we wrap into ErrorMessage.
        // WR-02: the prior ternary was `inner?.Message` ? message : $"{message}" — a
        // no-op whose interpolated branch returned `message` verbatim. Make the code
        // match the comment: include a redacted inner.Message in parentheses so the
        // eventual UI / log line carries the actionable root cause without leaking a
        // token. The RedactingHandler's WR-01 fix already redacts the inner chain's
        // Message text, so this redact is defence in depth.
        string safe = string.IsNullOrEmpty(inner?.Message)
            ? message
            : $"{message} ({TokenRedactor.Redact(inner.Message)})";
        return new UsageReading(
            Provider: "Z.ai",
            FetchedAtUtc: DateTimeOffset.UtcNow,
            Status: ReadingStatus.Error,
            UsedPct: null,
            RemainingPct: null,
            MostBindingWindow: default,
            AllWindows: null,
            ErrorMessage: safe);
    }
}
