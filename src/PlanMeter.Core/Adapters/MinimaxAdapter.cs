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
/// MINI-01 — MiniMax token-plan adapter. Issues <c>GET https://api.minimaxi.com/v1/token_plan/remains</c>
/// through the named "minimax" <see cref="HttpClient"/> so the SEC-02 allow-list +
/// SEC-03 redactor handlers run on every call. Never self-refreshes or mints a token
/// on 401 (SEC-01 prohibition #2).
///
/// Spike verdict (D-01/D-02): BRANCH A — the CN host api.minimaxi.com accepted the
/// user's coding-plan key with HTTP 200 and returned a per-model array of interval
/// (5h) and weekly remaining-percent fields. The fixture is pinned to the live capture
/// (D-03). Cookie session is NOT required — Bearer auth suffices.
///
/// The classification ladder mirrors ZaiAdapter verbatim:
/// empty key -> NotLoggedIn (zero HTTP); 401/403 -> NotLoggedIn; 429 -> Error + RetryAfter;
/// !IsSuccessStatusCode -> Error; 200-with-failure-envelope (base_resp.status_code != 0)
/// -> classify BEFORE normalizing (parse-once); malformed JSON -> Error;
/// HttpRequestException/TaskCanceledException -> Error.
/// </summary>
public sealed class MinimaxAdapter : IProviderAdapter
{
    private readonly IHttpClientFactory _factory;

    public MinimaxAdapter(IHttpClientFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>The machine identity — the key of the store / registry / poller slot (D-13).</summary>
    public ProviderId Id => "minimax";

    /// <summary>Human-readable provider name for the row and the <c>UsageReading.Provider</c> field.</summary>
    public string DisplayName => "MiniMax";

    /// <summary>MiniMax exposes a programmatic usage endpoint (spike-confirmed), so usage is fetchable.</summary>
    public bool SupportsUsageApi => true;

    /// <summary>
    /// D-10 — MiniMax requires a manually-entered API key stored via the per-provider DPAPI store.
    /// The settings window renders a key field for this adapter. Key is never read from opencode's
    /// auth.json (Pitfall 4 / A8).
    /// </summary>
    public bool RequiresManualKey => true;

    /// <summary>GRND-02 — MiniMax polls on schedule.</summary>
    public bool ManualOnlyFetch => false;

    /// <summary>GRND-02/D-07 — MiniMax uses a manual API key, not OAuth login.</summary>
    public bool SupportsOAuthLogin => false;

    /// <summary>Phase-4 groundwork — MiniMax authenticates with a manual API key.</summary>
    public AuthFamily AuthFamily => AuthFamily.Key;

    /// <summary>D-09 — the MiniMax console deep-link (the RE-LOGIN tooltip hyperlink).</summary>
    public string? ConsoleUrl => "https://platform.minimax.io/subscribe/token-plan";

    /// <summary>MiniMax's figure is an exact reading — no qualifier badge.</summary>
    public string? QualifierText => null;

    /// <summary>MiniMax supports usage polling — no Unsupported verdict tooltip.</summary>
    public string? UnsupportedReason => null;

    /// <summary>MiniMax is not a floor row — no floor tooltip.</summary>
    public string? FloorReason => null;

    /// <summary>
    /// D-16 — MiniMax is KEY-family with ConsoleUrl already parameterized; the existing
    /// BuildReLoginTooltip uses Adapter.DisplayName and Adapter.ConsoleUrl. Null = keep
    /// existing parameterized text.
    /// </summary>
    public string? ReLoginGuidance => null;

    /// <summary>
    /// PROV-03 presence probe (read-only — SEC-01): the MiniMax manual-key model is a
    /// DPAPI blob; Detect() is blob existence. The blob path is resolved through
    /// <see cref="DpapiKeyStore.ForProvider"/> — the one source of truth that reproduces
    /// the frozen <c>minimax.key.bin</c> path exactly (D-07). Never writes or mutates the
    /// credential path.
    /// </summary>
    public bool Detect() => File.Exists(DpapiKeyStore.ForProvider(Id).BlobPath);

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
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new AdapterFetchResult(new UsageReading(
                Provider: "MiniMax",
                FetchedAtUtc: DateTimeOffset.UtcNow,
                Status: ReadingStatus.NotLoggedIn,
                UsedPct: null,
                RemainingPct: null,
                MostBindingWindow: default,
                AllWindows: null,
                ErrorMessage: "MiniMax rejected the key."), null);
        }

        HttpClient http = _factory.CreateClient(HttpExtensions.MinimaxClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, HttpExtensions.MinimaxTokenPlanRemainsPath);
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
            return new AdapterFetchResult(ErrorReading("Couldn't reach MiniMax.", ex), null);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Timeout (the named client's 15s) — degrade to Error, never retry-storm.
            return new AdapterFetchResult(ErrorReading("Couldn't reach MiniMax.", ex), null);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new AdapterFetchResult(new UsageReading(
                    Provider: "MiniMax",
                    FetchedAtUtc: DateTimeOffset.UtcNow,
                    Status: ReadingStatus.NotLoggedIn,
                    UsedPct: null,
                    RemainingPct: null,
                    MostBindingWindow: default,
                    AllWindows: null,
                    ErrorMessage: "MiniMax rejected the key."), null);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new AdapterFetchResult(new UsageReading(
                    Provider: "MiniMax",
                    FetchedAtUtc: DateTimeOffset.UtcNow,
                    Status: ReadingStatus.NotLoggedIn,
                    UsedPct: null,
                    RemainingPct: null,
                    MostBindingWindow: default,
                    AllWindows: null,
                    ErrorMessage: "MiniMax rejected the key."), null);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } when
                        ? when - DateTimeOffset.UtcNow
                        : null);
                return new AdapterFetchResult(ErrorReading("MiniMax is unavailable right now.", null), retryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new AdapterFetchResult(ErrorReading("MiniMax is unavailable right now.", null), null);
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                return new AdapterFetchResult(ErrorReading("Couldn't read MiniMax's response.", ex), null);
            }

            // Parse the envelope ONCE — classify failure envelopes BEFORE normalizing.
            // MiniMax uses base_resp.status_code: 0 = success, non-zero = failure.
            // Auth-class failure codes map to NotLoggedIn; other non-zero codes map to Error.
            DateTimeOffset fetchedAt = DateTimeOffset.UtcNow;

            MinimaxEnvelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<MinimaxEnvelope>(body)
                    ?? new MinimaxEnvelope();
            }
            catch (JsonException ex)
            {
                return new AdapterFetchResult(ErrorReading("MiniMax sent a malformed response.", ex), null);
            }

            // Failure-envelope gate: base_resp.status_code != 0 means failure.
            // Parse once, classify, then hand the parsed envelope to the normalizer.
            if (envelope.BaseResp?.StatusCode != 0)
            {
                int statusCode = envelope.BaseResp?.StatusCode ?? -1;

                // Auth-class failure codes -> NotLoggedIn (same shape as HTTP 401/403).
                // Observed auth-class codes: 1004 (auth insufficient) — extend as discovered.
                if (statusCode == 401 || statusCode == 403 || statusCode == 1004)
                {
                    return new AdapterFetchResult(new UsageReading(
                        Provider: "MiniMax",
                        FetchedAtUtc: fetchedAt,
                        Status: ReadingStatus.NotLoggedIn,
                        UsedPct: null,
                        RemainingPct: null,
                        MostBindingWindow: default,
                        AllWindows: null,
                        ErrorMessage: "MiniMax rejected the key."), null);
                }

                return new AdapterFetchResult(ErrorReading("MiniMax rejected the request.", null), null);
            }

            return new AdapterFetchResult(MinimaxNormalizer.Normalize(envelope, fetchedAt), null);
        }
    }

    private static UsageReading ErrorReading(string message, Exception? inner)
    {
        // SEC-03: defensive — never let a raw exception message reach a logger.
        string safe = string.IsNullOrEmpty(inner?.Message)
            ? message
            : $"{message} ({TokenRedactor.Redact(inner.Message)})";
        return new UsageReading(
            Provider: "MiniMax",
            FetchedAtUtc: DateTimeOffset.UtcNow,
            Status: ReadingStatus.Error,
            UsedPct: null,
            RemainingPct: null,
            MostBindingWindow: default,
            AllWindows: null,
            ErrorMessage: safe);
    }
}
