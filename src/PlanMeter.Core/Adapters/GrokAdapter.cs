using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PlanMeter.Core.Credentials;
using PlanMeter.Core.Http;
using PlanMeter.Core.Models;

namespace PlanMeter.Core.Adapters;

/// <summary>
/// GROK-02 / GROK-04 — live Session-family Grok adapter. Posts an empty gRPC-web
/// frame on the named billing client, classifies headers-first then trailer-frame
/// gRPC failures, and maps a heuristic wire snapshot onto a single-window reading.
/// Detect is DPAPI-blob existence only (D-07). Auth-failure budget is exactly one
/// force-refresh plus one retry (GROK-05). Error messages are fixed strings —
/// never response body text.
/// </summary>
public sealed class GrokAdapter : IProviderAdapter
{
    private const string OriginValue = "https://grok.com";
    private const string RefererValue = "https://grok.com/?_s=usage";
    private const string GrpcWebContentType = "application/grpc-web+proto";
    private const string ConnectUserAgent = "connect-es/2.1.1";

    /// <summary>
    /// Internal seam for blob-existence Detect tests. Production default is the
    /// per-provider DPAPI blob path.
    /// </summary>
    internal static Func<bool> BlobExistsPredicate { get; set; } =
        static () => DpapiKeyStore.ForProvider("grok").BlobPathExists();

    private readonly IHttpClientFactory? _factory;
    private readonly IGrokTokenAccess? _tokens;

    /// <summary>Detect-only default ctor — fetch paths require the factory ctor.</summary>
    public GrokAdapter()
    {
    }

    /// <summary>Production ctor. The token manager is the sole credential owner.</summary>
    public GrokAdapter(IHttpClientFactory factory, GrokTokenManager tokens)
        : this(factory, (IGrokTokenAccess)tokens)
    {
    }

    /// <summary>Internal test ctor — injects a token-lifecycle stub.</summary>
    internal GrokAdapter(IHttpClientFactory factory, IGrokTokenAccess tokens)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    /// <summary>The machine identity — "grok".</summary>
    public ProviderId Id => "grok";

    /// <summary>Human-readable name for the row.</summary>
    public string DisplayName => "Grok";

    /// <summary>The live billing path is proven (D-10) — the row is fetchable.</summary>
    public bool SupportsUsageApi => true;

    /// <summary>Session family — no manual key card in settings.</summary>
    public bool RequiresManualKey => false;

    /// <summary>Grok is timer-polled by design.</summary>
    public bool ManualOnlyFetch => false;

    /// <summary>The settings login card ships with the live adapter.</summary>
    public bool SupportsOAuthLogin => true;

    /// <summary>Phase-4 groundwork — Grok authenticates with a PlanMeter-owned session.</summary>
    public AuthFamily AuthFamily => AuthFamily.Session;

    /// <summary>The post-Unsupported usage surface.</summary>
    public string? ConsoleUrl => OriginValue;

    /// <summary>
    /// UIR-03 — complete removal of the estimated qualifier from row, tooltip, and
    /// automation suffix. Heuristic parse honesty is unchanged (null snapshot still Errors).
    /// </summary>
    public string? QualifierText => null;

    /// <summary>The row is live, not Unsupported.</summary>
    public string? UnsupportedReason => null;

    /// <summary>Grok is never a floor row — FloorReason is null.</summary>
    public string? FloorReason => null;

    /// <summary>D-06 — re-login points at PlanMeter's own settings.</summary>
    public string? ReLoginGuidance => "Log in to xAI in PlanMeter Settings";

    /// <summary>D-07 — Detect is DPAPI-blob existence only.</summary>
    public bool Detect() => BlobExistsPredicate();

    /// <summary>On-demand test-fetch — same core as the poller path.</summary>
    public async Task<UsageReading> TestFetchAsync(string? apiKey, CancellationToken ct = default)
        => (await FetchCoreAsync(apiKey, ct).ConfigureAwait(false)).Reading;

    /// <summary>The poller's fetch path (timer-polled Session family).</summary>
    public Task<AdapterFetchResult> FetchUsageAsync(string? apiKey, CancellationToken ct = default)
        => FetchCoreAsync(apiKey, ct);

    private async Task<AdapterFetchResult> FetchCoreAsync(string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new AdapterFetchResult(NotLoggedInReading(), null);
        }

        if (_factory is null || _tokens is null)
        {
            throw new InvalidOperationException("GrokAdapter requires IHttpClientFactory to fetch usage.");
        }

        string? token = await _tokens.GetValidTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
        {
            return new AdapterFetchResult(NotLoggedInReading(), null);
        }

        return await AttemptAsync(token, allowRefresh: true, ct).ConfigureAwait(false);
    }

    private async Task<AdapterFetchResult> AttemptAsync(string token, bool allowRefresh, CancellationToken ct)
    {
        HttpClient http = _factory!.CreateClient(HttpExtensions.GrokBillingClientName);
        using var request = BuildBillingRequest(token);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return new AdapterFetchResult(ErrorReading("Couldn't reach Grok."), null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AdapterFetchResult(ErrorReading("Couldn't reach Grok."), null);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return await HandleAuthFailureAsync(allowRefresh, ct).ConfigureAwait(false);
            }

            if (TryReadGrpcStatus(response.Headers, out int headerStatus, out string headerMessage)
                && headerStatus != 0)
            {
                return await ClassifyGrpcFailureAsync(headerStatus, headerMessage, allowRefresh, ct)
                    .ConfigureAwait(false);
            }

            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                return new AdapterFetchResult(ErrorReading($"Grok is unavailable right now. ({code})"), null);
            }

            byte[] body;
            try
            {
                body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return new AdapterFetchResult(ErrorReading("Couldn't reach Grok."), null);
            }

            if (TryReadTrailerStatus(body, out int trailerStatus, out string trailerMessage)
                && trailerStatus != 0)
            {
                return await ClassifyGrpcFailureAsync(trailerStatus, trailerMessage, allowRefresh, ct)
                    .ConfigureAwait(false);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            GrokWireSnapshot? snapshot = GrokWireParser.TryParse(body, now);
            if (snapshot is null)
            {
                return new AdapterFetchResult(
                    ErrorReading("Grok returned data PlanMeter couldn't read."),
                    null);
            }

            return new AdapterFetchResult(OkReading(snapshot, now), null);
        }
    }

    private async Task<AdapterFetchResult> HandleAuthFailureAsync(bool allowRefresh, CancellationToken ct)
    {
        if (!allowRefresh)
        {
            return new AdapterFetchResult(NotLoggedInReading(), null);
        }

        string? refreshed = await _tokens!.ForceRefreshAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(refreshed))
        {
            return new AdapterFetchResult(NotLoggedInReading(), null);
        }

        return await AttemptAsync(refreshed, allowRefresh: false, ct).ConfigureAwait(false);
    }

    private Task<AdapterFetchResult> ClassifyGrpcFailureAsync(
        int status,
        string message,
        bool allowRefresh,
        CancellationToken ct)
    {
        string decoded = PercentDecode(message);
        string lower = decoded.ToLowerInvariant();

        if (status == 16 || (status == 7 && IsAuthFailureMessage(lower)))
        {
            return HandleAuthFailureAsync(allowRefresh, ct);
        }

        if (status == 9 && lower.Trim() == "no personal team")
        {
            return Task.FromResult(new AdapterFetchResult(
                ErrorReading("Grok billing isn't available for this account."),
                null));
        }

        if (status is 4 or 14)
        {
            return Task.FromResult(new AdapterFetchResult(
                ErrorReading("Grok is unavailable right now."),
                null));
        }

        if (status == 1 && (lower.Contains("timeout") || lower.Contains("deadline") || lower.Contains("expired")))
        {
            return Task.FromResult(new AdapterFetchResult(
                ErrorReading("Grok is unavailable right now."),
                null));
        }

        return Task.FromResult(new AdapterFetchResult(
            ErrorReading("Grok is unavailable right now."),
            null));
    }

    private static bool IsAuthFailureMessage(string lower)
    {
        if (lower.Contains("bad-credentials") || lower.Contains("unauthenticated"))
        {
            return true;
        }

        if (lower.Contains("oauth2") && lower.Contains("could not be validated"))
        {
            return true;
        }

        if (lower.Contains("access token")
            && (lower.Contains("invalid") || lower.Contains("expired") || lower.Contains("could not be validated")))
        {
            return true;
        }

        return false;
    }

    private static HttpRequestMessage BuildBillingRequest(string token)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(new Uri(HttpExtensions.GrokBillingBaseUrl), HttpExtensions.GrokBillingPath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Origin", OriginValue);
        request.Headers.TryAddWithoutValidation("Referer", RefererValue);
        request.Headers.Accept.Clear();
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("x-grpc-web", "1");
        request.Headers.TryAddWithoutValidation("x-user-agent", ConnectUserAgent);

        var content = new ByteArrayContent(new byte[5]);
        content.Headers.ContentType = new MediaTypeHeaderValue(GrpcWebContentType);
        request.Content = content;
        return request;
    }

    private static bool TryReadGrpcStatus(
        HttpHeaders headers,
        out int status,
        out string message)
    {
        status = 0;
        message = string.Empty;
        if (!headers.TryGetValues("grpc-status", out IEnumerable<string>? values))
        {
            return false;
        }

        string? raw = null;
        foreach (string value in values)
        {
            raw = value;
            break;
        }

        if (raw is null || !int.TryParse(raw.Trim(), out status))
        {
            return false;
        }

        if (headers.TryGetValues("grpc-message", out IEnumerable<string>? messages))
        {
            foreach (string value in messages)
            {
                message = value;
                break;
            }
        }

        return true;
    }

    private static bool TryReadTrailerStatus(byte[] body, out int status, out string message)
    {
        status = 0;
        message = string.Empty;
        int offset = 0;
        while (offset + 5 <= body.Length)
        {
            byte flags = body[offset];
            uint length = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(offset + 1, 4));
            if (length > int.MaxValue - 5)
            {
                return false;
            }

            int payloadLength = (int)length;
            if (offset + 5 + payloadLength > body.Length)
            {
                return false;
            }

            if ((flags & 0x80) != 0)
            {
                string text = Encoding.ASCII.GetString(body, offset + 5, payloadLength);
                if (TryParseTrailerText(text, out status, out message))
                {
                    return true;
                }
            }

            offset += 5 + payloadLength;
        }

        return false;
    }

    private static bool TryParseTrailerText(string text, out int status, out string message)
    {
        status = 0;
        message = string.Empty;
        bool found = false;
        string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        foreach (string line in lines)
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Equals("grpc-status", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, out int parsed))
            {
                status = parsed;
                found = true;
            }
            else if (name.Equals("grpc-message", StringComparison.OrdinalIgnoreCase))
            {
                message = value;
            }
        }

        return found;
    }

    private static string PercentDecode(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static UsageReading OkReading(GrokWireSnapshot snapshot, DateTimeOffset now)
    {
        double used = Math.Clamp(snapshot.UsedPercent, 0.0, 100.0);
        double remaining = 100.0 - used;
        DateTimeOffset? resetsAt = snapshot.ResetsAtUnixSeconds is long unix
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;
        WindowKind kind = GrokWireParser.KindForReset(resetsAt, now);
        ReadingStatus status = remaining <= UsageReading.NearLimitRemainingThreshold
            ? ReadingStatus.NearLimit
            : ReadingStatus.Ok;
        var window = new WindowReading(kind, used, remaining, resetsAt);
        return new UsageReading(
            Provider: "Grok",
            FetchedAtUtc: now,
            Status: status,
            UsedPct: used,
            RemainingPct: remaining,
            MostBindingWindow: kind,
            AllWindows: new[] { window },
            ErrorMessage: null);
    }

    private static UsageReading NotLoggedInReading() => new(
        Provider: "Grok",
        FetchedAtUtc: DateTimeOffset.UtcNow,
        Status: ReadingStatus.NotLoggedIn,
        UsedPct: null,
        RemainingPct: null,
        MostBindingWindow: default,
        AllWindows: null,
        ErrorMessage: null);

    private static UsageReading ErrorReading(string message) => new(
        Provider: "Grok",
        FetchedAtUtc: DateTimeOffset.UtcNow,
        Status: ReadingStatus.Error,
        UsedPct: null,
        RemainingPct: null,
        MostBindingWindow: default,
        AllWindows: null,
        ErrorMessage: message);
}
