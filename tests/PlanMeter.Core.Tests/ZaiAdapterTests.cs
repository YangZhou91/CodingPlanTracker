using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using PlanMeter.Core.Adapters;
using PlanMeter.Core.Http;
using PlanMeter.Core.Models;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// ZAI-01 — adapter tests against an in-process mock <see cref="HttpMessageHandler"/>.
/// No network. Covers success / 401 / network-failure (D-07).
/// </summary>
/// <remarks>
/// The tests construct a real <c>HttpClient</c> factory adapter by hand: the named
/// "zai" client's Polly retry policy is OUT of scope here (it lives in
/// <see cref="HttpExtensions.AddPlanMeterZaiClient"/>), so we feed the adapter a
/// plain <c>HttpClient</c> via a minimal <see cref="IHttpClientFactory"/> stub.
/// This keeps the unit test deterministic — the adapter maps the inner handler's
/// response into a <see cref="UsageReading"/>.
/// </remarks>
public sealed class ZaiAdapterTests
{
    [Fact]
    public async Task Success_200_with_body_returns_Ok_reading()
    {
        string body = /*lang=json,strict*/ """
        {
          "code": 200,
          "msg": "ok",
          "data": {
            "limits": [
              { "type": "TOKENS_LIMIT", "number": 5, "window": 18000, "used": 740, "limit": 1000 }
            ]
          },
          "success": true
        }
        """;
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });
        var adapter = new ZaiAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.Ok);
        reading.UsedPct.Should().BeApproximately(74.0, 0.01);
        reading.MostBindingWindow.Should().Be(WindowKind.FiveHour);
        reading.ErrorMessage.Should().BeNull();
        handler.CallCount.Should().Be(1);
        // 200-on-bad-key trap guard: this success:true body MUST stay Ok after the
        // envelope check in FetchCoreAsync (Task 1) — (Success || Code==200) is the
        // discriminator's pass condition, so a genuine success:true envelope sails
        // through to ZaiNormalizer and is rendered as real usage, not a reject.
    }

    [Fact]
    public async Task Unauthorized_401_returns_NotLoggedIn_and_never_self_refreshes()
    {
        // ZAI-01/401 truth + SEC-01 prohibition #2 — surface RE-LOGIN, NEVER self-refresh.
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var adapter = new ZaiAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.NotLoggedIn);
        reading.UsedPct.Should().BeNull();
        reading.ErrorMessage.Should().Contain("Z.ai rejected the key");
        handler.CallCount.Should().Be(1,
            "401 must NOT trigger a self-refresh / retry — exactly one request went out");
    }

    [Fact]
    public async Task Forbidden_403_returns_NotLoggedIn()
    {
        // Treat 403 the same as 401 — both surface RE-LOGIN (SEC-01: never retry into risk-control).
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.Forbidden));
        var adapter = new ZaiAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.NotLoggedIn);
    }

    [Fact]
    public async Task Network_failure_returns_Error()
    {
        var handler = new StubHandler(new HttpRequestException("simulated network down"));
        var adapter = new ZaiAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.Error);
        reading.UsedPct.Should().BeNull();
        reading.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Server_error_5xx_returns_Error()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var adapter = new ZaiAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.Error);
        reading.ErrorMessage.Should().Contain("Z.ai is unavailable");
    }

    [Fact]
    public async Task Empty_key_returns_NotLoggedIn_without_invoking_handler()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var adapter = new ZaiAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.NotLoggedIn);
        handler.CallCount.Should().Be(0,
            "an empty key must short-circuit before any HTTP request is issued");
    }

    [Fact]
    public async Task TestFetchAsync_shares_implementation_with_FetchUsageAsync()
    {
        // D-04 — TestFetchAsync is the on-save validation gate; it uses the same
        // endpoint + same handler chain as the poller's FetchUsageAsync.
        string body = /*lang=json,strict*/ """
        {
          "code": 200,
          "msg": "ok",
          "data": { "limits": [ { "type": "TOKENS_LIMIT", "number": 5, "window": 18000, "used": 100, "limit": 1000 } ] },
          "success": true
        }
        """;
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });
        var adapter = new ZaiAdapter(new StubFactory(handler));

        UsageReading reading = await adapter.TestFetchAsync("candidate-key", CancellationToken.None);

        reading.Status.Should().Be(ReadingStatus.Ok);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Bad_key_200_with_success_false_envelope_returns_NotLoggedIn()
    {
        // ZAI-01 200-on-bad-key trap (UAT Test 13 Deferred Follow-Up, promoted to a
        // real adapter fix): Z.ai returns HTTP 200 with a body envelope whose
        // success=false + code=401 signals the reject. Pre-fix this fell through to
        // ZaiNormalizer and was misclassified as a Q1 Ok-with-null-figure reading,
        // so a mistyped key silently showed "—" and got persisted to zai.key.bin.
        string body = /*lang=json,strict*/ """
        {
          "code": 401,
          "msg": "token expired or incorrect",
          "success": false
        }
        """;
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });
        var adapter = new ZaiAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("xai-INVALID-test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.NotLoggedIn,
            "an invalid key that Z.ai rejects with a 200 + success:false + code:401 envelope must surface RE-LOGIN (the 200-on-bad-key trap — previously misclassified as Ok)");
        reading.UsedPct.Should().BeNull();
        reading.ErrorMessage.Should().Contain("Z.ai rejected the key");
        handler.CallCount.Should().Be(1,
            "envelope-reject must NOT trigger a retry or self-refresh — exactly one request (SEC-01 prohibition #2)");
    }

    [Fact]
    public async Task Missing_auth_header_200_with_code_1001_returns_NotLoggedIn()
    {
        // Second observed Z.ai auth-failure envelope: code:1001 ("Authentication
        // parameter not received in Header, unable to authenticate"). Same surface
        // as the 401-envelope path — NotLoggedIn + null UsedPct.
        string body = /*lang=json,strict*/ """
        {
          "code": 1001,
          "msg": "Authentication parameter not received in Header, unable to authenticate",
          "success": false
        }
        """;
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });
        var adapter = new ZaiAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("xai-INVALID-test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.NotLoggedIn,
            "code:1001 is the missing-auth-header variant of the 200-on-bad-key trap — same NotLoggedIn classification");
        reading.UsedPct.Should().BeNull();
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Unknown_failure_code_200_with_success_false_returns_Error()
    {
        // Defensive — unrecognized success:false code (e.g. a future rate-limit /
        // server-error shape; none observed live). Must be Error, NOT silently Ok
        // and NOT NotLoggedIn (preserves the "anything we don't recognize is an
        // Error" invariant so a future shape never sneaks past as Q1-empty Ok).
        string body = /*lang=json,strict*/ """
        {
          "code": 429,
          "msg": "rate limited",
          "success": false
        }
        """;
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });
        var adapter = new ZaiAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.Error,
            "a success:false envelope with a code we don't recognize as auth-failure must be Error, not silently Ok and not NotLoggedIn — preserves the unrecognized-shape→Error invariant");
        reading.ErrorMessage.Should().NotBeNullOrEmpty();
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task TooManyRequests_429_with_RetryAfter_returns_Error_and_carries_retry_after()
    {
        // D-21 — the 429 branch: the classified reading is the same Error the generic
        // non-success branch produces, but the result ALSO carries the Retry-After
        // duration so the poller can extend its NEXT tick (REFRESH-03 cross-tick
        // backoff) without retry-storming the provider.
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3600)) },
        };
        var handler = new StubHandler(response);
        var adapter = new ZaiAdapter(new StubFactory(handler));

        AdapterFetchResult result = await adapter.FetchUsageAsync("test-key", CancellationToken.None);

        result.Reading.Status.Should().Be(ReadingStatus.Error,
            "429 → Error reading (the row shows the failure while the poller backs off)");
        result.Reading.ErrorMessage.Should().Contain("Z.ai is unavailable");
        result.RetryAfter.Should().Be(TimeSpan.FromHours(1),
            "the 429 Retry-After header must be carried on AdapterFetchResult for the poller's backoff");
        handler.CallCount.Should().Be(1,
            "429 must NOT trigger an in-tick retry — exactly one request (Polly has no 429 retry)");
    }

    [Fact]
    public void Port_conformance_manifest_and_detect()
    {
        // D-11/D-12 — the reference adapter conforms to the IProviderAdapter port: the
        // machine identity, the display name, supports_usage_api, and the read-only
        // Detect() presence probe (DPAPI blob existence — never writes).
        var adapter = new ZaiAdapter(new StubFactory(new StubHandler(new HttpResponseMessage(HttpStatusCode.OK))));

        adapter.Id.Should().Be(new ProviderId("zai"));
        adapter.DisplayName.Should().Be("Z.ai");
        adapter.SupportsUsageApi.Should().BeTrue();
        adapter.RequiresManualKey.Should().BeTrue(
            "Z.ai is API-key based — the settings window must render a key field for it (D-10)");
        adapter.Detect().Should().Be(System.IO.File.Exists(PlanMeter.Core.Credentials.DpapiKeyStore.DefaultBlobPath),
            "Detect() is a read-only File.Exists probe of the DPAPI blob path (SEC-01)");
    }

    /// <summary>
    /// Minimal <see cref="HttpMessageHandler"/> that returns a pre-canned response or
    /// throws a pre-canned exception. Counts invocations so tests can assert "the inner
    /// handler was invoked exactly once".
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        private readonly Exception? _exception;
        public int CallCount;

        public StubHandler(HttpResponseMessage response) { _response = response; }
        public StubHandler(Exception exception) { _exception = exception; _response = null!; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);

            // SEC-03 sanity: assert the request carries a Bearer Authorization header
            // (the redactor lives on the named-client chain; this stub is INSIDE the chain,
            // so the request still has its real Authorization).
            if (_response is not null)
            {
                return Task.FromResult(_response);
            }

            throw _exception!;
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name)
        {
            // SEC-02 contract: the named "zai" client is the only client the adapter
            // creates. Assert the right name is requested.
            name.Should().Be(HttpExtensions.ZaiClientName,
                "ZaiAdapter must only ever create the named 'zai' HttpClient (so the SEC-02 + SEC-03 handlers run)");
            return new HttpClient(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri(HttpExtensions.ZaiBaseUrl),
                Timeout = HttpExtensions.ZaiTimeout,
            };
        }
    }
}
