using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PlanMeter.Core.Auth;
using PlanMeter.Core.Http;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// GROK-03 (10-01 Task 1) — RFC 8628 device-flow service tests against an in-process
/// stub <see cref="HttpMessageHandler"/>. No network. Pins: the client_id/scope form
/// POST, interval default + expiry clamps, the RFC 8628 §3.5 error taxonomy
/// (authorization_pending / slow_down / expired_token / access_denied), the
/// missing-refresh-token hard error, malformed-body tolerance, discovery fallback to
/// the pinned endpoints, and the verification_uri host validation (phishing vector,
/// T-10-02). RESEARCH Pattern 1/2 + Pitfall 5 are the port contract.
/// </summary>
public sealed class GrokOAuthFlowTests
{
    private const string HappyDeviceCodeBody = /*lang=json,strict*/ """
    {
      "device_code": "DEV-abc123",
      "user_code": "ABCD-EFGH",
      "verification_uri": "https://auth.x.ai/activate",
      "expires_in": 600,
      "interval": 5
    }
    """;

    // ------------------------------------------------------------------
    // StartDeviceFlowAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task StartDeviceFlowAsync_happy_path_POSTs_client_id_and_scope_and_returns_code()
    {
        var handler = new RoutingHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return Json(404, "{}"); // discovery fails → pinned endpoints (covered in detail below)
            }

            return Json(200, HappyDeviceCodeBody);
        });
        var flow = new GrokOAuthFlow(new AuthFactory(handler));

        DeviceFlowStart start = await flow.StartDeviceFlowAsync(CancellationToken.None);

        start.Success.Should().BeTrue("a 200 device-code response with all fields is the happy path");
        start.DeviceCode.Should().Be("DEV-abc123");
        start.UserCode.Should().Be("ABCD-EFGH");
        start.VerificationUri.Should().Be("https://auth.x.ai/activate");
        start.ExpiresIn.Should().Be(600);
        start.IntervalSeconds.Should().Be(5);
        start.TokenEndpoint.Should().Be("https://auth.x.ai/oauth2/token",
            "with discovery failed the pinned token endpoint is used");

        RecordedRequest devicePost = handler.Requests
            .Should().ContainSingle(r => r.Method == HttpMethod.Post, "only the device-code POST happens")
            .Subject;
        devicePost.Uri!.AbsolutePath.Should().Be("/oauth2/device/code");

        var form = ParseForm(devicePost.Body);
        form["client_id"].Should().Be(GrokOAuthFlow.ClientId,
            "the pinned Grok CLI client_id (public client — 'none' token auth method, no secret)");
        form["scope"].Should().Be(GrokOAuthFlow.Scope,
            "the exact cc-switch-pinned scope string including offline_access");
        devicePost.Body.Should().NotContain("client_secret",
            "a public client never sends a secret");
    }

    [Fact]
    public async Task StartDeviceFlowAsync_defaults_interval_to_5_and_clamps_expires_in()
    {
        string noInterval = /*lang=json,strict*/ """
        {
          "device_code": "D1",
          "user_code": "U1",
          "verification_uri": "https://auth.x.ai/activate",
          "expires_in": 999999
        }
        """;
        string zeroExpiry = /*lang=json,strict*/ """
        {
          "device_code": "D2",
          "user_code": "U2",
          "verification_uri": "https://auth.x.ai/activate",
          "expires_in": 0,
          "interval": 3
        }
        """;

        var handlerNoInterval = new RoutingHandler(_ => Json(200, noInterval));
        var handlerZero = new RoutingHandler(_ => Json(200, zeroExpiry));

        DeviceFlowStart absentInterval = await new GrokOAuthFlow(new AuthFactory(handlerNoInterval))
            .StartDeviceFlowAsync(CancellationToken.None);
        DeviceFlowStart zero = await new GrokOAuthFlow(new AuthFactory(handlerZero))
            .StartDeviceFlowAsync(CancellationToken.None);

        absentInterval.IntervalSeconds.Should().Be(GrokOAuthFlow.DefaultIntervalSeconds,
            "RFC 8628 §3.5: absent interval defaults to 5s");
        absentInterval.ExpiresIn.Should().Be(86400,
            "expires_in is clamped to at most 24h (cc-switch clamp)");

        zero.ExpiresIn.Should().Be(1,
            "expires_in is clamped to at least 1s");
        zero.IntervalSeconds.Should().Be(3,
            "an advertised interval is honored as-is");
    }

    [Fact]
    public async Task Discovery_failure_falls_back_to_pinned_endpoints()
    {
        var handler = new RoutingHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return Json(500, "internal"); // discovery blows up entirely
            }

            return req.Uri!.AbsolutePath == "/oauth2/device/code"
                ? Json(200, HappyDeviceCodeBody)
                : Json(400, /*lang=json,strict*/ """ { "error": "authorization_pending" } """);
        });
        var flow = new GrokOAuthFlow(new AuthFactory(handler));

        DeviceFlowStart start = await flow.StartDeviceFlowAsync(CancellationToken.None);
        start.Success.Should().BeTrue("discovery failure must not block the flow — pinned endpoints take over");
        start.TokenEndpoint.Should().Be("https://auth.x.ai/oauth2/token");

        DeviceFlowStep step = await flow.PollTokenAsync(start, CancellationToken.None);
        step.Should().BeOfType<DeviceFlowStep.Pending>();

        RecordedRequest tokenPost = handler.Requests
            .Should().ContainSingle(r => r.Uri!.AbsolutePath == "/oauth2/token")
            .Subject;
        var form = ParseForm(tokenPost.Body);
        form["grant_type"].Should().Be(GrokOAuthFlow.DeviceGrantType);
        form["client_id"].Should().Be(GrokOAuthFlow.ClientId);
        form["device_code"].Should().Be("DEV-abc123");
    }

    [Fact]
    public async Task Discovery_success_uses_discovered_endpoints()
    {
        string discovery = /*lang=json,strict*/ """
        {
          "device_authorization_endpoint": "https://auth.x.ai/oauth2/device/custom-code",
          "token_endpoint": "https://auth.x.ai/oauth2/custom-token"
        }
        """;
        var handler = new RoutingHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return Json(200, discovery);
            }

            return Json(200, HappyDeviceCodeBody);
        });
        var flow = new GrokOAuthFlow(new AuthFactory(handler));

        DeviceFlowStart start = await flow.StartDeviceFlowAsync(CancellationToken.None);

        start.Success.Should().BeTrue();
        handler.Requests.Should().ContainSingle(r =>
            r.Method == HttpMethod.Post && r.Uri!.AbsolutePath == "/oauth2/device/custom-code",
            "the discovered device_authorization_endpoint wins over the pinned path");
        start.TokenEndpoint.Should().Be("https://auth.x.ai/oauth2/custom-token");
    }

    [Fact]
    public async Task StartDeviceFlowAsync_accepts_accounts_x_ai_verification_uri()
    {
        // LIVE-observed shape (2026-08-24): the auth.x.ai device endpoint returns the
        // verification page on https://accounts.x.ai/oauth2/device — xAI's SSO host.
        // The P5 trust rule is the x.ai domain, not the literal auth.x.ai host.
        string live = /*lang=json,strict*/ """
        {
          "device_code": "DEV-abc123",
          "user_code": "ABCD-EFGH",
          "verification_uri": "https://accounts.x.ai/oauth2/device",
          "verification_uri_complete": "https://accounts.x.ai/oauth2/device?user_code=ABCD-EFGH",
          "expires_in": 1800,
          "interval": 5
        }
        """;
        var handler = new RoutingHandler(req =>
            req.Method == HttpMethod.Get ? Json(404, "{}") : Json(200, live));
        var flow = new GrokOAuthFlow(new AuthFactory(handler));

        DeviceFlowStart start = await flow.StartDeviceFlowAsync(CancellationToken.None);

        start.Success.Should().BeTrue(
            "accounts.x.ai is xAI-owned — the verification page host is trusted");
        start.VerificationUri.Should().Be("https://accounts.x.ai/oauth2/device");
    }

    [Fact]
    public async Task StartDeviceFlowAsync_rejects_verification_uri_on_wrong_host()
    {
        string spoofed = /*lang=json,strict*/ """
        {
          "device_code": "DEV-abc123",
          "user_code": "ABCD-EFGH",
          "verification_uri": "https://evil.example/activate",
          "expires_in": 600
        }
        """;
        var handler = new RoutingHandler(req =>
            req.Method == HttpMethod.Get ? Json(404, "{}") : Json(200, spoofed));
        var flow = new GrokOAuthFlow(new AuthFactory(handler));

        DeviceFlowStart start = await flow.StartDeviceFlowAsync(CancellationToken.None);

        start.Success.Should().BeFalse(
            "a server-provided verification_uri NOT on auth.x.ai is a phishing vector (prohibition P5) — fail, never relay");
        start.FailureMessage.Should().NotBeNullOrWhiteSpace();
        start.DeviceCode.Should().BeEmpty("an unusable start must not leak a half-usable device code");
    }

    [Fact]
    public async Task StartDeviceFlowAsync_network_error_returns_fixed_string_failure()
    {
        var handler = new RoutingHandler(_ => throw new HttpRequestException("socket exploded"));
        var flow = new GrokOAuthFlow(new AuthFactory(handler));

        DeviceFlowStart start = await flow.StartDeviceFlowAsync(CancellationToken.None);

        start.Success.Should().BeFalse();
        start.FailureMessage.Should().Be(GrokOAuthFlow.NetworkErrorMessage,
            "network failures surface a FIXED human string — never the exception message (Pitfall 6)");
    }

    // ------------------------------------------------------------------
    // PollTokenAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task PollTokenAsync_authorization_pending_keeps_waiting()
    {
        var handler = new RoutingHandler(_ => Json(400, /*lang=json,strict*/ """
            { "error": "authorization_pending" }
            """));
        var flow = new GrokOAuthFlow(new AuthFactory(handler));

        DeviceFlowStep step = await flow.PollTokenAsync(NewStart(), CancellationToken.None);

        step.Should().BeOfType<DeviceFlowStep.Pending>(
            "authorization_pending means the user hasn't approved yet — keep polling at the advertised interval (D-04)");
    }

    [Fact]
    public async Task PollTokenAsync_slow_down_widens_interval()
    {
        var handler = new RoutingHandler(_ => Json(400, /*lang=json,strict*/ """
            { "error": "slow_down" }
            """));
        var flow = new GrokOAuthFlow(new AuthFactory(handler));

        DeviceFlowStep step = await flow.PollTokenAsync(NewStart(), CancellationToken.None);

        step.Should().BeOfType<DeviceFlowStep.SlowDown>(
            "slow_down tells the caller to widen the interval for this and all subsequent polls");
        GrokOAuthFlow.SlowDownIncrementSeconds.Should().Be(5,
            "the documented +5s increment (RFC 8628 §3.5)");
        GrokOAuthFlow.PollIntervalCapSeconds.Should().Be(63,
            "the documented 63s cap (cc-switch parity)");
    }

    [Fact]
    public async Task PollTokenAsync_expired_and_denied_are_terminal()
    {
        var expired = new RoutingHandler(_ => Json(400, /*lang=json,strict*/ """
            { "error": "expired_token" }
            """));
        var denied = new RoutingHandler(_ => Json(400, /*lang=json,strict*/ """
            { "error": "access_denied" }
            """));

        DeviceFlowStep expiredStep = await new GrokOAuthFlow(new AuthFactory(expired))
            .PollTokenAsync(NewStart(), CancellationToken.None);
        DeviceFlowStep deniedStep = await new GrokOAuthFlow(new AuthFactory(denied))
            .PollTokenAsync(NewStart(), CancellationToken.None);

        expiredStep.Should().BeOfType<DeviceFlowStep.Expired>(
            "expired_token is terminal — the card auto-reverts (D-04)");
        deniedStep.Should().BeOfType<DeviceFlowStep.Denied>(
            "access_denied is terminal — the user refused the code");
    }

    [Fact]
    public async Task PollTokenAsync_success_requires_refresh_token()
    {
        string noRefresh = /*lang=json,strict*/ """
        {
          "access_token": "eyJhbGciOi.stub.access",
          "expires_in": 3600
        }
        """;
        var handler = new RoutingHandler(_ => Json(200, noRefresh));
        var flow = new GrokOAuthFlow(new AuthFactory(handler));

        DeviceFlowStep step = await flow.PollTokenAsync(NewStart(), CancellationToken.None);

        step.Should().BeOfType<DeviceFlowStep.Failed>(
            "a device-grant success response WITHOUT refresh_token is a HARD error (Pitfall 5c) — PlanMeter cannot self-refresh without it");
        step.As<DeviceFlowStep.Failed>().Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PollTokenAsync_success_parses_tokens()
    {
        string full = /*lang=json,strict*/ """
        {
          "access_token": "eyJhbGciOi.stub.access",
          "refresh_token": "59EAstubRefreshtoken",
          "expires_in": 1800,
          "id_token": "eyJhbGciOi.stub.id"
        }
        """;
        string noExpires = /*lang=json,strict*/ """
        {
          "access_token": "eyJhbGciOi.stub.access2",
          "refresh_token": "59EAstubRefreshtoken2"
        }
        """;
        var fullHandler = new RoutingHandler(_ => Json(200, full));
        var noExpiresHandler = new RoutingHandler(_ => Json(200, noExpires));

        DeviceFlowStep fullStep = await new GrokOAuthFlow(new AuthFactory(fullHandler))
            .PollTokenAsync(NewStart(), CancellationToken.None);
        DeviceFlowStep noExpiresStep = await new GrokOAuthFlow(new AuthFactory(noExpiresHandler))
            .PollTokenAsync(NewStart(), CancellationToken.None);

        var complete = fullStep.Should().BeOfType<DeviceFlowStep.Complete>().Subject;
        complete.Tokens.AccessToken.Should().Be("eyJhbGciOi.stub.access");
        complete.Tokens.RefreshToken.Should().Be("59EAstubRefreshtoken");
        complete.Tokens.ExpiresInSeconds.Should().Be(1800);
        complete.Tokens.IdToken.Should().Be("eyJhbGciOi.stub.id",
            "id_token is absorbed but never parsed (D-03: no account-identity display)");

        var noExpiresComplete = noExpiresStep.Should().BeOfType<DeviceFlowStep.Complete>().Subject;
        noExpiresComplete.Tokens.ExpiresInSeconds.Should().BeNull(
            "a raw absent expires_in stays null — honesty at the boundary");
        noExpiresComplete.Tokens.EffectiveExpiresInSeconds.Should().Be(3600,
            "the 3600s default is surfaced for callers that don't want to re-derive it");
    }

    [Fact]
    public async Task PollTokenAsync_malformed_json_fails_without_throwing()
    {
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<html><body>gateway error MARKER-XYZ</body></html>",
                Encoding.UTF8,
                "text/html"),
        });
        var flow = new GrokOAuthFlow(new AuthFactory(handler));

        DeviceFlowStep step = await flow.PollTokenAsync(NewStart(), CancellationToken.None);

        var failed = step.Should().BeOfType<DeviceFlowStep.Failed>().Subject;
        failed.Message.Should().NotBeNullOrWhiteSpace(
            "a garbage 200 body fails with a fixed-string reason");
        failed.Message.Should().NotContainAny(
            new[] { "<html", "MARKER-XYZ", "gateway" },
            "the fixed string must NEVER carry body content (Pitfall 6 — no body logging)");
    }

    // ------------------------------------------------------------------
    // Named-client constants + registration (GRND-01: allow-list must not grow)
    // ------------------------------------------------------------------

    [Fact]
    public void Named_clients_registered_against_allow_listed_hosts()
    {
        HttpExtensions.GrokAuthClientName.Should().Be("grok-auth");
        HttpExtensions.GrokAuthBaseUrl.Should().Be("https://auth.x.ai/");
        HttpExtensions.GrokAuthTimeout.Should().Be(TimeSpan.FromSeconds(15));

        HttpExtensions.GrokBillingClientName.Should().Be("grok-billing");
        HttpExtensions.GrokBillingBaseUrl.Should().Be("https://grok.com/");
        HttpExtensions.GrokBillingTimeout.Should().Be(TimeSpan.FromSeconds(15));
        HttpExtensions.GrokBillingPath.Should().Be("grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig");

        HttpExtensions.ProviderAllowedHosts.Should().BeEquivalentTo(new[]
        {
            "api.z.ai", "api.minimaxi.com", "chatgpt.com", "auth.x.ai", "grok.com", "opencode.ai",
        }, "both grok hosts were ALREADY pinned in Phase 7 — this plan must not grow the array");

        var services = new ServiceCollection();
        services.AddPlanMeterGrokAuthClient();
        services.AddPlanMeterGrokBillingClient();
        using ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();

        factory.CreateClient(HttpExtensions.GrokAuthClientName).BaseAddress
            .Should().Be(new Uri("https://auth.x.ai/"));
        factory.CreateClient(HttpExtensions.GrokBillingClientName).BaseAddress
            .Should().Be(new Uri("https://grok.com/"));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static DeviceFlowStart NewStart() => new()
    {
        DeviceCode = "DEV-abc123",
        UserCode = "ABCD-EFGH",
        VerificationUri = "https://auth.x.ai/activate",
        TokenEndpoint = "https://auth.x.ai/oauth2/token",
        ExpiresIn = 600,
        IntervalSeconds = 5,
        IssuedAtUtc = DateTimeOffset.UtcNow,
    };

    private static HttpResponseMessage Json(int status, string body) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>
    /// Decodes an application/x-www-form-urlencoded body into a dictionary. Handles the
    /// '+'-as-space encoding FormUrlEncodedContent produces (Uri.UnescapeDataString alone
    /// does not).
    /// </summary>
    private static Dictionary<string, string> ParseForm(string? body) =>
        (body ?? string.Empty)
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                kv => Uri.UnescapeDataString(kv[0]),
                kv => kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : string.Empty);

    private sealed record RecordedRequest(HttpMethod Method, Uri? Uri, string? Body);

    /// <summary>
    /// Routes every request through a test-supplied function and records the
    /// method/URI/body for assertions (the request object may be disposed by the
    /// caller after SendAsync, so values are snapshotted at send time).
    /// </summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<RecordedRequest, HttpResponseMessage> _route;

        public RoutingHandler(Func<RecordedRequest, HttpResponseMessage> route)
        {
            _route = route;
        }

        public List<RecordedRequest> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string? body = request.Content is null
                ? null
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            var recorded = new RecordedRequest(request.Method, request.RequestUri, body);
            lock (Requests)
            {
                Requests.Add(recorded);
            }

            return Task.FromResult(_route(recorded));
        }
    }

    /// <summary>
    /// Stub factory asserting the flow only ever creates the named grok-auth client
    /// (the SEC-02 allow-list + SEC-03 redactor chain rides that registration in
    /// production — the stub stands INSIDE the chain like CodexAdapterTests).
    /// </summary>
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
                "GrokOAuthFlow must only ever create the named 'grok-auth' HttpClient");
            return new HttpClient(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri(HttpExtensions.GrokAuthBaseUrl),
                Timeout = HttpExtensions.GrokAuthTimeout,
            };
        }
    }
}
