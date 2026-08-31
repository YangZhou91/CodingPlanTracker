using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PlanMeter.Core.Http;

namespace PlanMeter.Core.Auth;

/// <summary>
/// GROK-03 — the RFC 8628 device-authorization-grant flow against <c>auth.x.ai</c>,
/// ported from the Rust reference implementation (RESEARCH Pattern 1/2). WPF-free Core service:
/// <see cref="StartDeviceFlowAsync"/> issues a device code (discovery-driven, pinned
/// fallback), <see cref="PollTokenAsync"/> classifies one token-poll round per the
/// RFC 8628 §3.5 taxonomy; the CALLER (settings card / live tracer) owns the polling
/// timer, the wall-clock expiry deadline, and cancellation (D-04, Pitfall 7).
/// </summary>
/// <remarks>
/// <para>REDACTION DISCIPLINE (Pitfall 6 / T-10-01): this class has NO logger and must
/// never gain one. The xAI refresh_token is an opaque 86-char string with NO
/// TokenRedactor shape coverage — every failure path emits a FIXED human string;
/// response bodies and tokens are consumed in memory and never surfaced. Error message
/// values must never interpolate a body, a token, or an exception message.</para>
/// <para>Public client (RFC 8628 §3.1.1 / discovery <c>token_endpoint_auth_methods_supported:
/// ["none"]</c>): every request is a form POST carrying <c>client_id</c> only — never a
/// secret.</para>
/// </remarks>
public sealed class GrokOAuthFlow
{
    /// <summary>
    /// The Grok CLI's public client_id (pinned from the Rust reference (xai_oauth_auth.rs);
    /// the same client the reference tool itself uses). Verified live against the discovery doc.
    /// </summary>
    public const string ClientId = "b1a00492-073a-47ea-816f-4c329264a828";

    /// <summary>
    /// The scope string pinned from the Rust reference — <c>offline_access</c> is load-bearing
    /// (it is what makes the token response carry a refresh_token).
    /// </summary>
    public const string Scope = "openid profile email offline_access grok-cli:access api:access";

    public const string DiscoveryPath = ".well-known/openid-configuration";

    /// <summary>The pinned device-authorization endpoint path (relative to auth.x.ai).</summary>
    public const string DeviceCodePath = "oauth2/device/code";

    /// <summary>The pinned token endpoint path (relative to auth.x.ai).</summary>
    public const string TokenPath = "oauth2/token";

    /// <summary>The RFC 8628 device-grant type used by every token poll.</summary>
    public const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>The refresh grant type used later by GrokTokenManager (constant lives here, next to its siblings).</summary>
    public const string RefreshGrantType = "refresh_token";

    /// <summary>
    /// RFC 8628 §3.5: on <c>slow_down</c> the interval MUST increase by 5 seconds for
    /// this and ALL subsequent polls. The CALLER applies the increment.
    /// </summary>
    public const int SlowDownIncrementSeconds = 5;

    /// <summary>The ceiling for the widened poll interval (reference parity).</summary>
    public const int PollIntervalCapSeconds = 63;

    /// <summary>RFC 8628 default poll interval when the server advertises none.</summary>
    public const int DefaultIntervalSeconds = 5;

    /// <summary>
    /// Fallback device-code lifetime when the response omits <c>expires_in</c>
    /// (RFC 8628 §3.2 recommends 600s). Clamped into [1s, 24h] like every advertised value.
    /// </summary>
    public const int DefaultDeviceCodeExpirySeconds = 600;

    /// <summary>Default access-token lifetime when a token response omits <c>expires_in</c>.</summary>
    public const int DefaultTokenExpirySeconds = 3600;

    /// <summary>
    /// The FIXED human string for any network failure (unreachable / timeout). Public so
    /// callers and tests can match it without duplicating copy (never log around it).
    /// </summary>
    public const string NetworkErrorMessage = "Couldn't reach xAI login.";

    private const string ExpectedHost = "auth.x.ai";    private const string RefusedMessage = "xAI refused the login request.";
    private const string NoCodeMessage = "xAI didn't issue a device code.";
    private const string BadVerificationUriMessage = "xAI returned an unexpected sign-in address.";
    private const string UnreadableResponseMessage = "xAI sent an unreadable login response.";
    private const string IncompleteResponseMessage = "xAI's login response was incomplete.";
    private const string LoginFailedMessage = "xAI login failed.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Tolerant DTO + JsonExtensionData: unknown fields are absorbed, never fatal.
    };

    private readonly IHttpClientFactory _factory;

    /// <param name="factory">Must create the named grok-auth client (the SEC-02 allow-list + SEC-03 redactor chain rides that registration).</param>
    public GrokOAuthFlow(IHttpClientFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>
    /// Issue a device code: one best-effort discovery GET (any failure falls back to the
    /// pinned endpoints), then the form POST. Never throws on network/parse problems —
    /// failures come back as a non-success <see cref="DeviceFlowStart"/> carrying a fixed
    /// human string. Real cancellation (<paramref name="ct"/>) propagates (D-04 Cancel).
    /// </summary>
    public async Task<DeviceFlowStart> StartDeviceFlowAsync(CancellationToken ct = default)
    {
        HttpClient client = _factory.CreateClient(HttpExtensions.GrokAuthClientName);

        string deviceEndpoint = DeviceCodePath;
        string tokenEndpoint = TokenPath;

        // Discovery — best-effort (GROK-03 names discovery; ANY failure → pinned fallback).
        try
        {
            using var discoveryRequest = new HttpRequestMessage(HttpMethod.Get, DiscoveryPath);
            using HttpResponseMessage discovery = await client.SendAsync(discoveryRequest, ct)
                .ConfigureAwait(false);
            if (discovery.IsSuccessStatusCode)
            {
                string discoveryBody = await discovery.Content.ReadAsStringAsync(ct)
                    .ConfigureAwait(false);
                Dictionary<string, JsonElement>? doc = TryParse(discoveryBody);

                if (IsTrustedEndpoint(GetString(doc, "device_authorization_endpoint"), out Uri? deviceUri))
                {
                    deviceEndpoint = deviceUri.AbsoluteUri;
                }

                if (IsTrustedEndpoint(GetString(doc, "token_endpoint"), out Uri? tokenUri))
                {
                    tokenEndpoint = tokenUri.AbsoluteUri;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // real cancellation propagates — the card's Cancel button
        }
        catch (HttpRequestException)
        {
            // best-effort: the pinned endpoints take over
        }
        catch (TaskCanceledException)
        {
            // timeout — same fallback
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, deviceEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["scope"] = Scope,
                }),
            };
            using HttpResponseMessage response = await client.SendAsync(request, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return DeviceFlowStart.Failed(RefusedMessage);
            }

            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Dictionary<string, JsonElement>? fields = TryParse(body);

            string? deviceCode = GetString(fields, "device_code");
            string? userCode = GetString(fields, "user_code");
            string? verificationUri = GetString(fields, "verification_uri");

            if (string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(userCode) ||
                string.IsNullOrEmpty(verificationUri))
            {
                return DeviceFlowStart.Failed(NoCodeMessage);
            }

            // Prohibition P5 (phishing vector): the verification_uri is server-provided —
            // validate it belongs to an xAI-owned host BEFORE the value is ever relayed
            // or auto-opened. LIVE-observed (2026-08-24): auth.x.ai's device endpoint
            // returns https://accounts.x.ai/oauth2/device (xAI's SSO host), so the trust
            // rule is the x.ai domain (only xAI controls subdomain creation).
            if (!Uri.TryCreate(verificationUri, UriKind.Absolute, out Uri? parsed)
                || !IsTrustedVerificationUri(parsed))
            {
                return DeviceFlowStart.Failed(BadVerificationUriMessage);
            }

            int expires = Math.Clamp(
                GetInt(fields, "expires_in") ?? DefaultDeviceCodeExpirySeconds,
                1,
                86400);
            int interval = Math.Max(1, GetInt(fields, "interval") ?? DefaultIntervalSeconds);

            // Normalize the resolved token endpoint (pinned relative or discovered
            // absolute) against the client base so the start always carries an
            // absolute URI for later polls.
            Uri tokenUri = new Uri(new Uri(HttpExtensions.GrokAuthBaseUrl), tokenEndpoint);

            return new DeviceFlowStart
            {
                DeviceCode = deviceCode,
                UserCode = userCode,
                VerificationUri = parsed.AbsoluteUri,
                TokenEndpoint = tokenUri.AbsoluteUri,
                ExpiresIn = expires,
                IntervalSeconds = interval,
                IssuedAtUtc = DateTimeOffset.UtcNow,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return DeviceFlowStart.Failed(NetworkErrorMessage);
        }
        catch (TaskCanceledException)
        {
            // HttpClient timeout surfaces as TaskCanceledException with ct not requested
            return DeviceFlowStart.Failed(NetworkErrorMessage);
        }
    }

    /// <summary>
    /// One token-poll round against <see cref="DeviceFlowStart.TokenEndpoint"/>. The
    /// caller drives the cadence (<see cref="DeviceFlowStart.IntervalSeconds"/>, widened
    /// on <see cref="DeviceFlowStep.SlowDown"/>) and the wall-clock expiry deadline.
    /// Never throws on network/parse problems; real cancellation propagates.
    /// </summary>
    public async Task<DeviceFlowStep> PollTokenAsync(DeviceFlowStart start, CancellationToken ct = default)
    {
        if (start is null)
        {
            throw new ArgumentNullException(nameof(start));
        }

        HttpClient client = _factory.CreateClient(HttpExtensions.GrokAuthClientName);

        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, start.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = DeviceGrantType,
                    ["client_id"] = ClientId,
                    ["device_code"] = start.DeviceCode,
                }),
            };
            using HttpResponseMessage response = await client.SendAsync(request, ct)
                .ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return new DeviceFlowStep.Failed(NetworkErrorMessage);
        }
        catch (TaskCanceledException)
        {
            return new DeviceFlowStep.Failed(NetworkErrorMessage);
        }

        Dictionary<string, JsonElement>? fields = TryParse(body);
        if (fields is null)
        {
            // 200-with-HTML / empty / garbage — fixed string, never body content.
            return new DeviceFlowStep.Failed(UnreadableResponseMessage);
        }

        string? error = GetString(fields, "error");
        if (error is not null)
        {
            // RFC 8628 §3.5 taxonomy (D-04).
            return error switch
            {
                "authorization_pending" => new DeviceFlowStep.Pending(),
                "slow_down" => new DeviceFlowStep.SlowDown(),
                "expired_token" => new DeviceFlowStep.Expired(),
                "access_denied" => new DeviceFlowStep.Denied(),
                _ => new DeviceFlowStep.Failed(LoginFailedMessage),
            };
        }

        string? accessToken = GetString(fields, "access_token");
        string? refreshToken = GetString(fields, "refresh_token");

        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
        {
            // Pitfall 5c: a device-grant success WITHOUT refresh_token is a HARD error —
            // PlanMeter cannot self-refresh without it; silence here would brick the login.
            return new DeviceFlowStep.Failed(IncompleteResponseMessage);
        }

        return new DeviceFlowStep.Complete(new TokenSet
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresInSeconds = GetInt(fields, "expires_in"),
            IdToken = GetString(fields, "id_token"), // absorbed, never parsed (D-03)
        });
    }

    private static Dictionary<string, JsonElement>? TryParse(string body)
    {
        try
        {
            TolerantJson? parsed = JsonSerializer.Deserialize<TolerantJson>(body, JsonOptions);
            return parsed?.Data;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // empty / whitespace body
            return null;
        }
    }

    private static string? GetString(Dictionary<string, JsonElement>? fields, string key) =>
        fields is not null
        && fields.TryGetValue(key, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(Dictionary<string, JsonElement>? fields, string key)
    {
        if (fields is null || !fields.TryGetValue(key, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out int number) => number,
            _ => null,
        };
    }

    /// <summary>
    /// A discovered endpoint is only trusted when absolute HTTPS on auth.x.ai — a
    /// discovery response pointing anywhere else is ignored in favor of the pinned paths
    /// (defense in depth below the allow-list, which would refuse the egress anyway).
    /// </summary>
    private static bool IsTrustedEndpoint(string? endpoint, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        return Uri.TryCreate(endpoint, UriKind.Absolute, out uri!)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, ExpectedHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// T-10-02 — a verification_uri is safe to relay / auto-open iff it is HTTPS on an
    /// xAI-owned host (<c>x.ai</c> or any <c>*.x.ai</c> subdomain — xAI alone controls
    /// subdomain creation, so the phishing property holds). The LIVE device endpoint
    /// returns <c>https://accounts.x.ai/oauth2/device</c> (observed 2026-08-24).
    /// </summary>
    public static bool IsTrustedVerificationUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && (string.Equals(uri.Host, "x.ai", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".x.ai", StringComparison.OrdinalIgnoreCase));

    /// <summary>Tolerant DTO — every field arrives via JsonExtensionData, nothing is fatal.</summary>
    private sealed class TolerantJson
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Data { get; set; }
    }
}

/// <summary>
/// The result of <see cref="GrokOAuthFlow.StartDeviceFlowAsync"/>: on success the device
/// code to poll, the user code + URI to show the user, the advertised poll interval, and
/// the resolved token endpoint; on failure a fixed human string. Properties are
/// init-settable so a driver (the live tracer) can reconstruct an in-flight start across
/// processes from persisted state.
/// </summary>
public sealed class DeviceFlowStart
{
    public string? FailureMessage { get; init; }

    public string DeviceCode { get; init; } = "";

    public string UserCode { get; init; } = "";

    /// <summary>Host-validated (xAI-owned: x.ai / *.x.ai) absolute URI — safe to display and auto-open.</summary>
    public string VerificationUri { get; init; } = "";

    /// <summary>Absolute token endpoint resolved at start (discovered or pinned).</summary>
    public string TokenEndpoint { get; init; } = "";

    /// <summary>Device-code lifetime in seconds, clamped to [1, 86400].</summary>
    public int ExpiresIn { get; init; } = GrokOAuthFlow.DefaultDeviceCodeExpirySeconds;

    /// <summary>Advertised poll interval in seconds (default 5; caller widens on slow_down).</summary>
    public int IntervalSeconds { get; init; } = GrokOAuthFlow.DefaultIntervalSeconds;

    /// <summary>Wall-clock issue time — expiry is <c>IssuedAtUtc + ExpiresIn</c> seconds (Pitfall 7).</summary>
    public DateTimeOffset IssuedAtUtc { get; init; }

    public bool Success => FailureMessage is null;

    public static DeviceFlowStart Failed(string message) => new() { FailureMessage = message };
}

/// <summary>
/// The discriminated result of ONE <see cref="GrokOAuthFlow.PollTokenAsync"/> round. The
/// caller maps each step to card behavior (D-04): Pending keeps waiting, SlowDown widens
/// by <see cref="GrokOAuthFlow.SlowDownIncrementSeconds"/> (cap
/// <see cref="GrokOAuthFlow.PollIntervalCapSeconds"/>), Expired/Denied revert the card,
/// Failed surfaces the fixed message.
/// </summary>
public abstract record DeviceFlowStep
{
    private DeviceFlowStep()
    {
    }

    /// <summary>The user hasn't approved yet — keep polling at the current interval.</summary>
    public sealed record Pending : DeviceFlowStep;

    /// <summary>Rate-limited — widen the interval by +5s for this and all subsequent polls (cap 63s).</summary>
    public sealed record SlowDown : DeviceFlowStep;

    /// <summary>Success — the complete token set (refresh_token guaranteed non-empty).</summary>
    public sealed record Complete(TokenSet Tokens) : DeviceFlowStep;

    /// <summary>Terminal — the device code expired; revert the card with the expired message.</summary>
    public sealed record Expired : DeviceFlowStep;

    /// <summary>Terminal — the user denied the code.</summary>
    public sealed record Denied : DeviceFlowStep;

    /// <summary>Terminal — fixed human string; never carries body/token content.</summary>
    public sealed record Failed(string Message) : DeviceFlowStep;
}

/// <summary>
/// A token response. <see cref="ExpiresInSeconds"/> stays null when the server omitted
/// it — callers who don't want to re-derive the default use
/// <see cref="EffectiveExpiresInSeconds"/> (3600s).
/// </summary>
public sealed record TokenSet
{
    public required string AccessToken { get; init; }

    /// <summary>Non-empty on every Complete step (the flow hard-fails without it).</summary>
    public required string RefreshToken { get; init; }

    /// <summary>Raw server value; null when absent (never fabricated here).</summary>
    public int? ExpiresInSeconds { get; init; }

    /// <summary>Absorbed, never parsed — no account-identity display (D-03).</summary>
    public string? IdToken { get; init; }

    public int EffectiveExpiresInSeconds =>
        ExpiresInSeconds is > 0 ? ExpiresInSeconds.Value : GrokOAuthFlow.DefaultTokenExpirySeconds;
}
