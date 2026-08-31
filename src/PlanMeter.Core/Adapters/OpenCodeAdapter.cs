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
/// OPC-02 — OpenCode GO live adapter. Issues <c>GET https://opencode.ai/zen/go/v1/usage</c>
/// through the named "opencode" <see cref="HttpClient"/> so the SEC-02 allow-list +
/// SEC-03 redactor handlers run on every call. Requires a manually-entered API key
/// (Bearer auth) stored in the DPAPI blob for provider "opencode".
///
/// Follows ZaiAdapter pattern exactly (the reference adapter).
/// </summary>
public sealed class OpenCodeAdapter : IProviderAdapter
{
    private readonly IHttpClientFactory _factory;
    private int _fetchCount;

    public OpenCodeAdapter(IHttpClientFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>The machine identity — the key of the store / registry / poller slot (D-13).</summary>
    public ProviderId Id => "opencode";

    /// <summary>Human-readable provider name for the row and the <c>UsageReading.Provider</c> field.</summary>
    public string DisplayName => "OpenCode GO";

    /// <summary>D-10 — OpenCode GO exposes a programmatic usage endpoint.</summary>
    public bool SupportsUsageApi => true;

    /// <summary>D-10 — OpenCode GO is API-key based; the settings window renders a key field.</summary>
    public bool RequiresManualKey => true;

    /// <summary>GRND-02 — OpenCode GO polls on schedule, not manual-only.</summary>
    public bool ManualOnlyFetch => false;

    /// <summary>GRND-02/D-07 — OpenCode GO uses a manual API key, not OAuth login.</summary>
    public bool SupportsOAuthLogin => false;

    /// <summary>D-10 — OpenCode GO authenticates with a manual API key.</summary>
    public AuthFamily AuthFamily => AuthFamily.Key;

    /// <summary>D-09 — the OpenCode GO console deep-link.</summary>
    public string? ConsoleUrl => "https://opencode.ai/";

    /// <summary>OpenCode GO's figure is an exact reading — no qualifier badge.</summary>
    public string? QualifierText => null;

    /// <summary>OpenCode GO supports usage polling — no Unsupported verdict tooltip.</summary>
    public string? UnsupportedReason => null;

    /// <summary>OpenCode GO is not a floor row — no floor tooltip.</summary>
    public string? FloorReason => null;

    /// <summary>
    /// D-16 — OpenCode GO is KEY-family with ConsoleUrl already parameterized; null = keep
    /// existing parameterized text.
    /// </summary>
    public string? ReLoginGuidance => null;

    /// <summary>
    /// PROV-03 presence probe: DPAPI blob existence for the "opencode" provider.
    /// Identical to ZaiAdapter.Detect().
    /// </summary>
    public bool Detect() => File.Exists(DpapiKeyStore.ForProvider(Id).BlobPath);

    /// <summary>
    /// The number of fetch invocations. Test seam.
    /// </summary>
    public int FetchCount => Volatile.Read(ref _fetchCount);

    /// <summary>
    /// D-04 — test-fetch BEFORE persisting the key.
    /// </summary>
    public async Task<UsageReading> TestFetchAsync(string? apiKey, CancellationToken ct = default)
        => (await FetchCoreAsync(apiKey, ct).ConfigureAwait(false)).Reading;

    /// <summary>
    /// The poller's fetch path.
    /// </summary>
    public Task<AdapterFetchResult> FetchUsageAsync(string? apiKey, CancellationToken ct = default)
        => FetchCoreAsync(apiKey, ct);

    private async Task<AdapterFetchResult> FetchCoreAsync(string? apiKey, CancellationToken ct)
    {
        Interlocked.Increment(ref _fetchCount);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new AdapterFetchResult(new UsageReading(
                Provider: "OpenCode GO",
                FetchedAtUtc: DateTimeOffset.UtcNow,
                Status: ReadingStatus.NotLoggedIn,
                UsedPct: null,
                RemainingPct: null,
                MostBindingWindow: default,
                AllWindows: null,
                ErrorMessage: "OpenCode GO rejected the key."), null);
        }

        HttpClient http = _factory.CreateClient(HttpExtensions.OpenCodeClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, HttpExtensions.OpenCodeUsagePath);
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
            return new AdapterFetchResult(ErrorReading("Couldn't reach OpenCode GO.", ex), null);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return new AdapterFetchResult(ErrorReading("Couldn't reach OpenCode GO.", ex), null);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new AdapterFetchResult(new UsageReading(
                    Provider: "OpenCode GO",
                    FetchedAtUtc: DateTimeOffset.UtcNow,
                    Status: ReadingStatus.NotLoggedIn,
                    UsedPct: null,
                    RemainingPct: null,
                    MostBindingWindow: default,
                    AllWindows: null,
                    ErrorMessage: "OpenCode GO rejected the key."), null);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new AdapterFetchResult(new UsageReading(
                    Provider: "OpenCode GO",
                    FetchedAtUtc: DateTimeOffset.UtcNow,
                    Status: ReadingStatus.NotLoggedIn,
                    UsedPct: null,
                    RemainingPct: null,
                    MostBindingWindow: default,
                    AllWindows: null,
                    ErrorMessage: "OpenCode GO rejected the key."), null);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } when
                        ? when - DateTimeOffset.UtcNow
                        : null);
                return new AdapterFetchResult(ErrorReading("OpenCode GO is unavailable right now.", null), retryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new AdapterFetchResult(ErrorReading("OpenCode GO is unavailable right now.", null), null);
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                return new AdapterFetchResult(ErrorReading("Couldn't read OpenCode GO's response.", ex), null);
            }

            DateTimeOffset fetchedAt = DateTimeOffset.UtcNow;

            OpenCodeUsageEnvelope envelope;
            try
            {
                // `?? new OpenCodeUsageEnvelope()` mirrors ZaiAdapter: a literal "null"
                // body deserializes to null and falls into the normalizer's Q1 path
                // (Ok-with-null-figure) instead of tripping nullable warnings.
                envelope = JsonSerializer.Deserialize<OpenCodeUsageEnvelope>(body) ?? new OpenCodeUsageEnvelope();
            }
            catch (JsonException ex)
            {
                return new AdapterFetchResult(ErrorReading("OpenCode GO sent a malformed response.", ex), null);
            }

            return new AdapterFetchResult(OpenCodeNormalizer.Normalize(envelope, fetchedAt), null);
        }
    }

    private static UsageReading ErrorReading(string message, Exception? inner)
    {
        string safe = string.IsNullOrEmpty(inner?.Message)
            ? message
            : $"{message} ({TokenRedactor.Redact(inner.Message)})";
        return new UsageReading(
            Provider: "OpenCode GO",
            FetchedAtUtc: DateTimeOffset.UtcNow,
            Status: ReadingStatus.Error,
            UsedPct: null,
            RemainingPct: null,
            MostBindingWindow: default,
            AllWindows: null,
            ErrorMessage: safe);
    }
}