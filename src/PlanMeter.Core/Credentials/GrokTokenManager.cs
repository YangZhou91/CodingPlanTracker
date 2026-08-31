using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PlanMeter.Core.Auth;
using PlanMeter.Core.Http;
using PlanMeter.Core.Models;

namespace PlanMeter.Core.Credentials;

/// <summary>
/// GROK-05 — PlanMeter's own Grok token lifecycle over the DPAPI store
/// (<see cref="DpapiKeyStore.ForProvider"/>("grok")): load, buffered refresh with
/// mutex-serialized rotation, and a terminal ReAuthRequired state. Behavioral port of
/// the reference XaiOAuthManager (RESEARCH Pattern 6).
/// </summary>
/// <remarks>
/// <para>BLOB SHAPE (discretion resolved): the store holds ONE DPAPI-encrypted JSON
/// string <c>{access_token, refresh_token, expires_at_unix_seconds}</c>. PlanMeter
/// reads/writes ONLY this blob — never the reference tool's token file
/// (prohibition P3: a shared refresh token invalidates both holders).</para>
/// <para>REDACTION DISCIPLINE (Pitfall 6 / T-10-01): NO logger in this class. The
/// 86-char xAI refresh token has NO TokenRedactor shape coverage — token values and
/// response bodies are consumed in memory and never surfaced anywhere.</para>
/// <para>AUTH-FAILURE BUDGET (prohibition P4): exactly one refresh per caller. Terminal
/// signals (401/403, 400-with-malformed-body, invalid_grant/invalid_token) flip
/// <see cref="ReAuthRequired"/> and the manager returns null forever after until
/// <see cref="SaveInitialTokensAsync"/> or <see cref="Logout"/> resets it — never an
/// auto-retry loop into xAI risk-control.</para>
/// </remarks>
public sealed class GrokTokenManager : IGrokTokenAccess
{
    /// <summary>
    /// The clock-skew buffer: refresh fires when <c>now &gt; expires_at − 60s</c>.
    /// 60s is the proven reference value.
    /// </summary>
    public const int RefreshBufferSeconds = 60;

    private const int DefaultExpiresInSeconds = 3600;

    private static readonly JsonSerializerOptions BlobJsonOptions = new();

    private readonly IHttpClientFactory _factory;
    private readonly DpapiKeyStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private TokenBlob? _cache;

    /// <param name="factory">Must create the named grok-auth client (SEC-02 + SEC-03 chain).</param>
    /// <param name="store">DPAPI store; production default is <c>ForProvider("grok")</c>, tests inject a temp directory.</param>
    /// <param name="clock">Time source; production <see cref="DateTimeOffset.UtcNow"/>, tests pin it.</param>
    public GrokTokenManager(IHttpClientFactory factory, DpapiKeyStore? store = null, Func<DateTimeOffset>? clock = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _store = store ?? DpapiKeyStore.ForProvider("grok");
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>True once a terminal auth failure occurred — the "Please re-login" state (GROK-05).</summary>
    internal bool ReAuthRequired { get; private set; }

    /// <summary>Whether a DPAPI blob exists (blob-existence Detect signal, D-07).</summary>
    public bool HasStoredToken => _store.BlobPathExists();

    /// <summary>
    /// Persist a freshly-completed device login (the flow guarantees a non-empty
    /// RefreshToken on Complete). Primes the in-memory cache, clears ReAuthRequired,
    /// writes the blob.
    /// </summary>
    public Task SaveInitialTokensAsync(TokenSet tokens, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        if (string.IsNullOrEmpty(tokens.RefreshToken))
        {
            throw new ArgumentException(
                "TokenSet.RefreshToken must be non-empty — PlanMeter cannot self-refresh without it.",
                nameof(tokens));
        }

        var blob = new TokenBlob
        {
            AccessToken = tokens.AccessToken ?? string.Empty,
            RefreshToken = tokens.RefreshToken,
            ExpiresAtUnixSeconds = _clock().ToUnixTimeSeconds() + tokens.EffectiveExpiresInSeconds,
        };

        _cache = blob;
        ReAuthRequired = false;
        Persist(blob);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The poller/login path: the cached access token while fresh (now ≤ expires_at −
    /// <see cref="RefreshBufferSeconds"/>), else ONE mutex-serialized refresh with a
    /// double-checked cache re-read (concurrent callers reuse the winner's refresh).
    /// Returns null when no blob exists or the manager is ReAuthRequired.
    /// </summary>
    public async Task<string?> GetValidTokenAsync(CancellationToken ct = default)
    {
        if (ReAuthRequired)
        {
            return null;
        }

        TokenBlob? cache = _cache ?? TryReadBlob();
        if (cache is null)
        {
            return null;
        }

        if (IsFresh(cache))
        {
            return cache.AccessToken;
        }

        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await GetValidUnderMutexAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Refresh REGARDLESS of cache age — the adapter's 401-retry path (exactly one
    /// refresh + one retry is the whole budget). Same mutex, same terminal taxonomy.
    /// </summary>
    public async Task<string?> ForceRefreshAsync(CancellationToken ct = default)
    {
        if (ReAuthRequired)
        {
            return null;
        }

        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await GetValidUnderMutexAsync(ct, forceRefresh: true).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Clear the blob + all in-memory state (D-03 Logout). Local clear only — matches
    /// the reference behavior (no revocation POST).
    /// </summary>
    public void Logout()
    {
        try
        {
            _store.Clear();
        }
        catch (IOException)
        {
            // blob deletion is best-effort; state still resets below
        }

        _cache = null;
        ReAuthRequired = false;
    }

    /// <summary>
    /// PASSIVE read for <see cref="GrokCredentialSource"/>: the STORED access token iff
    /// a readable blob exists — possibly stale, never refreshed, zero HTTP. This is the
    /// Pitfall 3 contract: non-null + a NotLoggedIn reading is what makes the poller's
    /// session-park discriminator fire.
    /// </summary>
    public string? PeekStoredAccessToken()
    {
        TokenBlob? blob = _cache ?? TryReadBlob();
        return blob is null || string.IsNullOrEmpty(blob.AccessToken) ? null : blob.AccessToken;
    }

    private async Task<string?> GetValidUnderMutexAsync(CancellationToken ct, bool forceRefresh = false)
    {
        if (ReAuthRequired)
        {
            return null;
        }

        // Double-checked (T-10-03): the mutex winner may have refreshed while we waited.
        TokenBlob? cache = _cache ?? TryReadBlob();
        if (cache is null)
        {
            return null;
        }

        if (!forceRefresh && IsFresh(cache))
        {
            return cache.AccessToken;
        }

        return await RefreshAsync(cache, ct).ConfigureAwait(false);
    }

    private bool IsFresh(TokenBlob blob) =>
        _clock().ToUnixTimeSeconds() <= blob.ExpiresAtUnixSeconds - RefreshBufferSeconds;

    /// <summary>POST the refresh grant; classify + apply. Caller MUST hold the mutex.</summary>
    private async Task<string?> RefreshAsync(TokenBlob cache, CancellationToken ct)
    {
        HttpClient client = _factory.CreateClient(HttpExtensions.GrokAuthClientName);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, GrokOAuthFlow.TokenPath)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = GrokOAuthFlow.RefreshGrantType,
                    ["client_id"] = GrokOAuthFlow.ClientId,
                    ["refresh_token"] = cache.RefreshToken,
                    ["scope"] = GrokOAuthFlow.Scope,
                }),
            };
            using HttpResponseMessage response = await client.SendAsync(request, ct)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return ClassifyAndApply(response.StatusCode, body, cache);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // real cancellation propagates
        }
        catch (HttpRequestException)
        {
            // Transient — degrade to the last cached token when one exists (the upstream
            // fetch surfaces its own error); never terminal, never a retry.
            return DegradeToCached(cache);
        }
        catch (TaskCanceledException)
        {
            return DegradeToCached(cache);
        }
    }

    /// <summary>
    /// The Pattern 6 taxonomy. Terminal: 401/403, or 400 whose body is not a readable
    /// error JSON (empty/HTML/garbage — Pitfall 5c), or JSON error invalid_grant /
    /// invalid_token. Success: commit access token + expiry; commit the rotated refresh
    /// token ONLY when non-empty AND different. Everything else: transient degrade.
    /// </summary>
    private string? ClassifyAndApply(HttpStatusCode status, string body, TokenBlob cache)
    {
        Dictionary<string, JsonElement>? fields = TryParse(body);
        string? error = GetString(fields, "error");

        bool terminal =
            status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || (status == HttpStatusCode.BadRequest && (fields is null || error is null))
            || error is "invalid_grant" or "invalid_token";

        if (terminal)
        {
            // GROK-05: "Please re-login" — a terminal flag, not an error loop.
            ReAuthRequired = true;
            return null;
        }

        string? accessToken = GetString(fields, "access_token");
        if ((int)status is >= 200 and <= 299 && !string.IsNullOrEmpty(accessToken))
        {
            long expiresAt = _clock().ToUnixTimeSeconds()
                + Math.Max(1, GetInt(fields, "expires_in") ?? DefaultExpiresInSeconds);

            // Rotation commit (Pitfall 5a): ONLY non-empty AND different. A response
            // without refresh_token leaves the stored one untouched — absence never bricks.
            string? candidate = GetString(fields, "refresh_token");
            string refresh = !string.IsNullOrEmpty(candidate) && candidate != cache.RefreshToken
                ? candidate
                : cache.RefreshToken;

            var updated = new TokenBlob
            {
                AccessToken = accessToken,
                RefreshToken = refresh,
                ExpiresAtUnixSeconds = expiresAt,
            };

            _cache = updated;
            Persist(updated);
            return accessToken;
        }

        // Unexpected shape (non-success with a readable non-terminal error, or success
        // without a usable access token) — transient: degrade, never fabricate.
        return DegradeToCached(cache);
    }

    private static string? DegradeToCached(TokenBlob cache) =>
        string.IsNullOrEmpty(cache.AccessToken) ? null : cache.AccessToken;

    private void Persist(TokenBlob blob)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _store.Protect(JsonSerializer.Serialize(blob, BlobJsonOptions));
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            // Never throw on IO/DPAPI failure — the in-memory cache serves this session;
            // the next process start degrades honestly to no blob (re-login).
        }
    }

    /// <summary>Read + decrypt + parse the blob. Null when absent/corrupt/unreadable — never throws.</summary>
    private TokenBlob? TryReadBlob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string? json = _store.ReadFresh();
        if (json is null)
        {
            return null;
        }

        try
        {
            TokenBlob? blob = JsonSerializer.Deserialize<TokenBlob>(json, BlobJsonOptions);
            if (blob is null || string.IsNullOrEmpty(blob.RefreshToken))
            {
                return null;
            }

            return blob;
        }
        catch (JsonException)
        {
            // Corrupt or unrelated blob content — degrade honestly to "no stored login".
            return null;
        }
    }

    private static Dictionary<string, JsonElement>? TryParse(string body)
    {
        try
        {
            TolerantJson? parsed = JsonSerializer.Deserialize<TolerantJson>(body);
            return parsed?.Data;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
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

    /// <summary>The DPAPI blob payload (one encrypted string holds this JSON).</summary>
    private sealed class TokenBlob
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; } = string.Empty;

        [JsonPropertyName("expires_at_unix_seconds")]
        public long ExpiresAtUnixSeconds { get; set; }
    }

    private sealed class TolerantJson
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Data { get; set; }
    }
}
