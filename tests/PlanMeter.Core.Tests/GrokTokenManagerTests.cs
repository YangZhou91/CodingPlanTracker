using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using PlanMeter.Core.Auth;
using PlanMeter.Core.Credentials;
using PlanMeter.Core.Http;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// GROK-05 (10-01 Task 2) — DPAPI-backed token manager with mutex-serialized rotation
/// refresh, plus the passive credential source that keeps the poller's session-park
/// discriminator alive (Pitfall 3). No network: refresh POSTs hit an in-process queued
/// stub; DPAPI runs against temp-directory blobs; time is a pinned injectable clock.
/// Port contract: RESEARCH Pattern 6 + Pitfall 5 (rotation-absence, 400-malformed).
/// </summary>
public sealed class GrokTokenManagerTests : IDisposable
{
    private readonly string _tempDir;

    public GrokTokenManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"grok-token-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    private DpapiKeyStore NewStore() =>
        new(Path.Combine(_tempDir, $"grok-{Guid.NewGuid():N}.key.bin"));

    // ------------------------------------------------------------------
    // Fresh-cache fast path
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveInitialTokens_persists_and_GetValidToken_returns_without_HTTP()
    {
        var handler = new QueuedHandler();
        DateTimeOffset now = FixedNow();
        var manager = new GrokTokenManager(new AuthFactory(handler), NewStore(), () => now);

        await manager.SaveInitialTokensAsync(Tokens("A1", "R1", 3600));

        manager.HasStoredToken.Should().BeTrue("SaveInitialTokens persists the DPAPI blob");

        string? token = await manager.GetValidTokenAsync();

        token.Should().Be("A1");
        handler.RequestBodies.Should().BeEmpty(
            "a cached token with future expiry performs ZERO HTTP — the fresh-cache fast path");
    }

    // ------------------------------------------------------------------
    // Buffered refresh
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetValidToken_refreshes_once_when_inside_60s_buffer()
    {
        var handler = new QueuedHandler(Success("A2", "R1", 3600));
        DateTimeOffset now = FixedNow();
        var manager = new GrokTokenManager(new AuthFactory(handler), NewStore(), () => now);

        // expires at now+30s — inside the 60s refresh buffer → one refresh fires.
        await manager.SaveInitialTokensAsync(Tokens("A1", "R1", 30));

        string? token = await manager.GetValidTokenAsync();

        token.Should().Be("A2");
        handler.RequestBodies.Should().HaveCount(1);
        handler.RequestBodies[0].Should().Contain("grant_type=refresh_token");
        handler.RequestBodies[0].Should().Contain("refresh_token=R1");
        handler.RequestBodies[0].Should().Contain($"client_id={GrokOAuthFlow.ClientId}");
        handler.RequestBodies[0].Should().Contain($"scope={Uri.EscapeDataString(GrokOAuthFlow.Scope).Replace("%20", "+")}",
            "the refresh form mirrors the reference shape (grant_type + client_id + refresh_token + scope)");
    }

    [Fact]
    public async Task Concurrent_GetValidToken_calls_issue_exactly_one_refresh()
    {
        var handler = new QueuedHandler(Success("A2", "R1", 3600));
        DateTimeOffset now = FixedNow();
        var manager = new GrokTokenManager(new AuthFactory(handler), NewStore(), () => now);

        await manager.SaveInitialTokensAsync(Tokens("A1", "R1", 30));

        string?[] results = await Task.WhenAll(
            manager.GetValidTokenAsync(),
            manager.GetValidTokenAsync(),
            manager.GetValidTokenAsync());

        results.Should().OnlyContain(t => t == "A2");
        handler.RequestBodies.Should().HaveCount(1,
            "SemaphoreSlim(1,1) + double-checked cache: concurrent callers reuse the winner's refresh (GROK-05 mutex, T-10-03)");
    }

    // ------------------------------------------------------------------
    // Rotation commit rules (Pitfall 5a)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Refresh_rotation_commits_only_nonempty_and_different()
    {
        var handler = new QueuedHandler(
            Success("A2", "R2", 3600), // refresh #1: rotates R1 -> R2
            Success("A3", null, 3600), // refresh #2: response WITHOUT refresh_token
            Success("A4", null, 3600)); // refresh #3: fresh manager re-reading the blob
        DateTimeOffset now = FixedNow();
        DpapiKeyStore store = NewStore();
        var manager = new GrokTokenManager(new AuthFactory(handler), store, () => now);

        await manager.SaveInitialTokensAsync(Tokens("A1", "R1", 30));
        (await manager.GetValidTokenAsync()).Should().Be("A2");

        now = now.AddSeconds(3700); // expired again
        (await manager.GetValidTokenAsync()).Should().Be("A3");

        // A brand-new manager over the same blob proves the STORE still holds R2 after
        // the no-rotation response — rotation absence never bricks the login.
        now = now.AddSeconds(3700);
        var rehydrated = new GrokTokenManager(new AuthFactory(handler), store, () => now);
        (await rehydrated.GetValidTokenAsync()).Should().Be("A4");

        handler.RequestBodies[0].Should().Contain("refresh_token=R1");
        handler.RequestBodies[1].Should().Contain("refresh_token=R2",
            "the rotated refresh token WAS committed and used next");
        handler.RequestBodies[2].Should().Contain("refresh_token=R2",
            "a response WITHOUT refresh_token leaves the stored one untouched");
    }

    // ------------------------------------------------------------------
    // Terminal-invalid taxonomy (GROK-05 / Pitfall 5c)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Invalid_grant_sets_ReAuthRequired_no_auto_retry()
    {
        var handler = new QueuedHandler(Error400("invalid_grant"));
        DateTimeOffset now = FixedNow();
        var manager = new GrokTokenManager(new AuthFactory(handler), NewStore(), () => now);

        await manager.SaveInitialTokensAsync(Tokens("A1", "R1", 30));

        (await manager.GetValidTokenAsync()).Should().BeNull(
            "invalid_grant → null: the 'Please re-login' precondition, never an error loop");
        manager.ReAuthRequired.Should().BeTrue();

        (await manager.GetValidTokenAsync()).Should().BeNull();
        handler.RequestBodies.Should().HaveCount(1,
            "ReAuthRequired never auto-retries — one refresh is the entire auth-failure budget (prohibition P4)");
    }

    [Fact]
    public async Task Refresh_401_403_and_400_malformed_body_are_terminal()
    {
        StubResponse[] terminalCases =
        {
            new(401, "{}"),
            new(403, "{}"),
            new(400, "<html>bad gateway</html>", "text/html"),
            new(400, ""),
        };

        foreach (StubResponse terminal in terminalCases)
        {
            var handler = new QueuedHandler(terminal);
            DateTimeOffset now = FixedNow();
            var manager = new GrokTokenManager(new AuthFactory(handler), NewStore(), () => now);
            await manager.SaveInitialTokensAsync(Tokens("A1", "R1", 30));

            (await manager.GetValidTokenAsync()).Should().BeNull(
                $"HTTP {terminal.Status} with a terminal body shape transitions to ReAuthRequired (Pitfall 5c)");
            manager.ReAuthRequired.Should().BeTrue();
            handler.RequestBodies.Should().HaveCount(1, "no retry after a terminal signal");
        }
    }

    // ------------------------------------------------------------------
    // Expiry math
    // ------------------------------------------------------------------

    [Fact]
    public async Task Expires_in_absent_defaults_3600()
    {
        var handler = new QueuedHandler(Success("A2", "R1", null));
        DateTimeOffset now = FixedNow();
        var manager = new GrokTokenManager(new AuthFactory(handler), NewStore(), () => now);

        await manager.SaveInitialTokensAsync(Tokens("A1", "R1", 30));
        (await manager.GetValidTokenAsync()).Should().Be("A2");

        now = now.AddSeconds(3000); // 3000 <= 3600-60 → still fresh under the 3600 default
        (await manager.GetValidTokenAsync()).Should().Be("A2");

        handler.RequestBodies.Should().HaveCount(1,
            "expires_in absent → the buffer math used the 3600s default — no second refresh at +3000s");
    }

    // ------------------------------------------------------------------
    // Logout
    // ------------------------------------------------------------------

    [Fact]
    public async Task Logout_clears_blob_and_resets_state()
    {
        var handler = new QueuedHandler(Error400("invalid_grant"));
        DateTimeOffset now = FixedNow();
        DpapiKeyStore store = NewStore();
        var manager = new GrokTokenManager(new AuthFactory(handler), store, () => now);
        var source = new GrokCredentialSource(manager);

        await manager.SaveInitialTokensAsync(Tokens("A1", "R1", 30));
        (await manager.GetValidTokenAsync()).Should().BeNull(); // → ReAuthRequired
        manager.ReAuthRequired.Should().BeTrue();

        manager.Logout();

        manager.HasStoredToken.Should().BeFalse("Logout clears the DPAPI blob");
        (await source.ReadFreshAsync()).Should().BeNull(
            "after Logout the source reads null again — NO LOGIN semantics restored");
        manager.ReAuthRequired.Should().BeFalse("Logout resets the terminal state");

        // A fresh login works again — the terminal flag must not outlive the blob.
        await manager.SaveInitialTokensAsync(Tokens("A9", "R9", 3600));
        manager.HasStoredToken.Should().BeTrue();
        (await manager.GetValidTokenAsync()).Should().Be("A9");
        handler.RequestBodies.Should().HaveCount(1,
            "no further HTTP after the re-login — the fresh cache serves");
    }

    // ------------------------------------------------------------------
    // Credential source (Pitfall 3 — park discriminator contract)
    // ------------------------------------------------------------------

    [Fact]
    public async Task CredentialSource_returns_null_iff_no_blob()
    {
        var handler = new QueuedHandler();
        DateTimeOffset now = FixedNow();
        var manager = new GrokTokenManager(new AuthFactory(handler), NewStore(), () => now);
        var source = new GrokCredentialSource(manager);

        (await source.ReadFreshAsync()).Should().BeNull(
            "no blob → null (the poller renders NO LOGIN pre-login)");

        await manager.SaveInitialTokensAsync(Tokens("A1", "R1", 30));

        (await source.ReadFreshAsync()).Should().Be("A1",
            "blob exists → the stored access token, possibly stale");

        now = now.AddSeconds(120); // stored token now long past expiry
        (await source.ReadFreshAsync()).Should().Be("A1",
            "lifecycle refresh is NOT the source's job — a possibly-stale token still reads non-null so the session-park discriminator keeps firing (Pitfall 3)");

        handler.RequestBodies.Should().BeEmpty("the credential source never performs HTTP");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static DateTimeOffset FixedNow() => new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static TokenSet Tokens(string access, string refresh, int? expiresInSeconds) => new()
    {
        AccessToken = access,
        RefreshToken = refresh,
        ExpiresInSeconds = expiresInSeconds,
    };

    private static StubResponse Success(string access, string? refresh, int? expiresInSeconds) =>
        new(200, BuildTokenJson(access, refresh, expiresInSeconds));

    private static StubResponse Error400(string error) =>
        new(400, $$""" { "error": "{{error}}" } """);

    private static string BuildTokenJson(string access, string? refresh, int? expiresInSeconds)
    {
        var parts = new List<string> { $"\"access_token\": \"{access}\"" };
        if (refresh is not null)
        {
            parts.Add($"\"refresh_token\": \"{refresh}\"");
        }

        if (expiresInSeconds is not null)
        {
            parts.Add($"\"expires_in\": {expiresInSeconds}");
        }

        return "{ " + string.Join(", ", parts) + " }";
    }

    private sealed record StubResponse(int Status, string Body, string MediaType = "application/json");

    /// <summary>
    /// Queued responses replayed in order (the last response replays when the queue is
    /// exhausted, so unexpected extra requests surface via RequestBodies count
    /// assertions instead of crashing the run).
    /// </summary>
    private sealed class QueuedHandler : HttpMessageHandler
    {
        private readonly Queue<StubResponse> _responses = new();
        private StubResponse? _last;

        public QueuedHandler(params StubResponse[] responses)
        {
            foreach (StubResponse response in responses)
            {
                _responses.Enqueue(response);
            }
        }

        public List<string> RequestBodies { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());

            StubResponse next;
            if (_responses.Count > 0)
            {
                next = _responses.Dequeue();
                _last = next;
            }
            else
            {
                next = _last!;
            }

            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)next.Status)
            {
                Content = new StringContent(next.Body, Encoding.UTF8, next.MediaType),
            });
        }
    }

    private sealed class AuthFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public AuthFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
        {
            name.Should().Be(HttpExtensions.GrokAuthClientName,
                "GrokTokenManager must only ever create the named 'grok-auth' HttpClient (SEC-02 + SEC-03 chain)");
            return new HttpClient(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri(HttpExtensions.GrokAuthBaseUrl),
                Timeout = HttpExtensions.GrokAuthTimeout,
            };
        }
    }
}
