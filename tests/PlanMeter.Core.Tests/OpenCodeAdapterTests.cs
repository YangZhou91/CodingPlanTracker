using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using PlanMeter.Core.Adapters;
using PlanMeter.Core.Credentials;
using PlanMeter.Core.Http;
using PlanMeter.Core.Models;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// OPC-02 (08-01) — OpenCode GO live adapter tests against an in-process mock
/// <see cref="HttpMessageHandler"/>. No network. Mirrors the ZaiAdapterTests structure:
/// manifest conformance, null-key short-circuit, 401/403 → NotLoggedIn, 429 → Error +
/// RetryAfter carrier, 5xx/network → Error, 200 happy path, and the D-07/D-08
/// envelope gates (all-non-OK → Error; malformed JSON → Error).
/// </summary>
/// <remarks>
/// The named "opencode" client's Polly retry policy is OUT of scope here (it lives in
/// <see cref="HttpExtensions.AddPlanMeterOpenCodeClient"/>); the adapter is fed a plain
/// <see cref="HttpClient"/> via a minimal <see cref="IHttpClientFactory"/> stub so the
/// unit tests stay deterministic.
/// </remarks>
public sealed class OpenCodeAdapterTests
{
    private const string HappyBody = /*lang=json,strict*/ """
    {
      "usage": {
        "rolling": { "status": "ok", "percent": 1, "resetsAt": "2026-08-19T15:30:00Z" },
        "weekly": { "status": "ok", "percent": 5, "resetsAt": "2026-08-26T00:00:00Z" },
        "monthly": { "status": "ok", "percent": 47, "resetsAt": "2026-09-01T00:00:00Z" }
      }
    }
    """;

    private static HttpResponseMessage OkJsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    [Fact]
    public void Port_conformance_manifest_values()
    {
        // D-10/D-11 — the live adapter's manifest: usage API on, manual key required,
        // KEY auth family, console deep-link, no qualifier/floor/unsupported chrome.
        var adapter = new OpenCodeAdapter(new StubFactory(new StubHandler(new HttpResponseMessage(HttpStatusCode.OK))));

        adapter.Id.Should().Be(new ProviderId("opencode"));
        adapter.DisplayName.Should().Be("OpenCode GO");
        adapter.SupportsUsageApi.Should().BeTrue(
            "OPC-02 flips the floor row to a live usage adapter — the poller must construct for it");
        adapter.RequiresManualKey.Should().BeTrue(
            "the settings window renders the existing key card via RequiresManualKey (D-10)");
        adapter.AuthFamily.Should().Be(AuthFamily.Key,
            "OpenCode GO authenticates with a manual Bearer API key (D-10)");
        adapter.ConsoleUrl.Should().Be("https://opencode.ai/",
            "KEY-family rows deep-link to the provider console (D-10)");
        adapter.QualifierText.Should().BeNull(
            "OpenCode's percents are exact readings — no 'estimated' badge (D-11)");
        adapter.FloorReason.Should().BeNull("the floor row is torn down (D-09)");
        adapter.UnsupportedReason.Should().BeNull("the row is no longer Unsupported (D-09)");
        adapter.ReLoginGuidance.Should().BeNull("KEY family uses ConsoleUrl (D-16)");
        adapter.ManualOnlyFetch.Should().BeFalse("OpenCode GO polls on the standard schedule");
        adapter.SupportsOAuthLogin.Should().BeFalse("no OAuth login flow for OpenCode GO");
        adapter.Detect().Should().Be(File.Exists(DpapiKeyStore.ForProvider(new ProviderId("opencode")).BlobPath),
            "Detect() is a read-only File.Exists probe of the DPAPI blob path — identical to ZaiAdapter (D-10)");
    }

    [Fact]
    public async Task Null_or_empty_key_returns_NotLoggedIn_without_invoking_handler()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var adapter = new OpenCodeAdapter(new StubFactory(handler));

        UsageReading nullKey = (await adapter.FetchUsageAsync(null, CancellationToken.None)).Reading;
        UsageReading emptyKey = (await adapter.FetchUsageAsync("", CancellationToken.None)).Reading;

        nullKey.Status.Should().Be(ReadingStatus.NotLoggedIn);
        nullKey.UsedPct.Should().BeNull();
        nullKey.ErrorMessage.Should().Contain("OpenCode GO rejected the key");
        emptyKey.Status.Should().Be(ReadingStatus.NotLoggedIn);
        handler.CallCount.Should().Be(0,
            "a null/empty key must short-circuit before any HTTP request is issued");
    }

    [Fact]
    public async Task Unauthorized_401_returns_NotLoggedIn()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var adapter = new OpenCodeAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.NotLoggedIn);
        reading.UsedPct.Should().BeNull();
        reading.ErrorMessage.Should().Contain("OpenCode GO rejected the key");
        handler.CallCount.Should().Be(1, "401 must NOT trigger a retry — exactly one request went out");
    }

    [Fact]
    public async Task Forbidden_403_returns_NotLoggedIn()
    {
        // Treat 403 the same as 401 — both surface RE-LOGIN (SEC-01: never retry into risk-control).
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.Forbidden));
        var adapter = new OpenCodeAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.NotLoggedIn);
        reading.ErrorMessage.Should().Contain("OpenCode GO rejected the key");
    }

    [Fact]
    public async Task TooManyRequests_429_returns_Error_and_carries_RetryAfter()
    {
        // The classified reading is Error; the result ALSO carries the Retry-After
        // duration so the poller can extend its NEXT tick (cross-tick backoff).
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(600)) },
        };
        var handler = new StubHandler(response);
        var adapter = new OpenCodeAdapter(new StubFactory(handler));

        AdapterFetchResult result = await adapter.FetchUsageAsync("test-key", CancellationToken.None);

        result.Reading.Status.Should().Be(ReadingStatus.Error);
        result.Reading.ErrorMessage.Should().Contain("OpenCode GO is unavailable");
        result.RetryAfter.Should().Be(TimeSpan.FromMinutes(10),
            "the 429 Retry-After header must be carried on AdapterFetchResult for the poller's backoff");
        handler.CallCount.Should().Be(1, "429 must NOT trigger an in-tick retry");
    }

    [Fact]
    public async Task Server_error_5xx_returns_Error_without_RetryAfter()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var adapter = new OpenCodeAdapter(new StubFactory(handler));

        AdapterFetchResult result = await adapter.FetchUsageAsync("test-key", CancellationToken.None);

        result.Reading.Status.Should().Be(ReadingStatus.Error);
        result.Reading.ErrorMessage.Should().Contain("OpenCode GO is unavailable");
        result.RetryAfter.Should().BeNull("only 429 carries the RetryAfter carrier");
    }

    [Fact]
    public async Task Success_200_happy_body_returns_Ok_reading_with_Monthly_figure()
    {
        // D-06 argmin(remaining): rolling 99% rem / weekly 95% rem / monthly 53% rem
        // → monthly is most-binding even though rolling is the least-used window.
        var handler = new StubHandler(() => OkJsonResponse(HappyBody));
        var adapter = new OpenCodeAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.Ok);
        reading.UsedPct.Should().BeApproximately(47.0, 0.01);
        reading.RemainingPct.Should().BeApproximately(53.0, 0.01);
        reading.MostBindingWindow.Should().Be(WindowKind.Monthly);
        reading.AllWindows.Should().NotBeNull();
        reading.AllWindows!.Count.Should().Be(3);
        reading.ErrorMessage.Should().BeNull();
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task All_non_ok_envelope_on_200_returns_Error_D08_gate()
    {
        // D-08 gate: a 200 whose every window status is non-"ok" classifies as Error —
        // never an Ok-with-figure from a failure envelope.
        string body = /*lang=json,strict*/ """
        {
          "usage": {
            "rolling": { "status": "exceeded", "percent": 100 },
            "weekly": { "status": "exceeded", "percent": 100 },
            "monthly": { "status": "exceeded", "percent": 100 }
          }
        }
        """;
        var handler = new StubHandler(() => OkJsonResponse(body));
        var adapter = new OpenCodeAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.Error,
            "an all-non-OK envelope must surface Error, not Ok (D-08 / T-08-05)");
        reading.ErrorMessage.Should().Contain("no usable window data");
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Malformed_json_on_200_returns_Error()
    {
        var handler = new StubHandler(() => OkJsonResponse("{usage:{rolling:{percent:\"not-a-number\"}}"));
        var adapter = new OpenCodeAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.Error);
        reading.ErrorMessage.Should().Contain("malformed response");
        reading.UsedPct.Should().BeNull();
    }

    [Fact]
    public async Task Network_failure_returns_Error()
    {
        var handler = new StubHandler(new HttpRequestException("simulated network down"));
        var adapter = new OpenCodeAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.Error);
        reading.ErrorMessage.Should().Contain("Couldn't reach OpenCode GO");
        reading.UsedPct.Should().BeNull();
    }

    [Fact]
    public async Task TestFetchAsync_shares_implementation_with_FetchUsageAsync()
    {
        // D-04 — TestFetchAsync is the on-save validation gate; it uses the same
        // endpoint + classification as the poller's FetchUsageAsync. Two adapter
        // instances (one per path) because each fetch disposes its response.
        var testAdapter = new OpenCodeAdapter(new StubFactory(new StubHandler(() => OkJsonResponse(HappyBody))));
        var pollAdapter = new OpenCodeAdapter(new StubFactory(new StubHandler(() => OkJsonResponse(HappyBody))));

        UsageReading testReading = await testAdapter.TestFetchAsync("candidate-key", CancellationToken.None);
        UsageReading pollReading = (await pollAdapter.FetchUsageAsync("candidate-key", CancellationToken.None)).Reading;

        testReading.Status.Should().Be(pollReading.Status);
        testReading.UsedPct.Should().Be(pollReading.UsedPct);
        testReading.MostBindingWindow.Should().Be(pollReading.MostBindingWindow);
        testAdapter.FetchCount.Should().Be(1);
        pollAdapter.FetchCount.Should().Be(1);
    }

    [Fact]
    public async Task FetchCount_increments_per_fetch()
    {
        var handler = new StubHandler(() => OkJsonResponse(HappyBody));
        var adapter = new OpenCodeAdapter(new StubFactory(handler));

        adapter.FetchCount.Should().Be(0);
        await adapter.FetchUsageAsync("test-key", CancellationToken.None);
        await adapter.FetchUsageAsync("test-key", CancellationToken.None);

        adapter.FetchCount.Should().Be(2, "each fetch invocation increments the seam exactly once");
    }

    /// <summary>
    /// Minimal <see cref="HttpMessageHandler"/> that returns a pre-canned response (a
    /// fresh instance per call when built from a factory — the adapter disposes each
    /// response it reads) or throws a pre-canned exception. Counts invocations so tests
    /// can assert "the inner handler was invoked exactly once".
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage>? _responseFactory;
        private readonly Exception? _exception;
        public int CallCount;

        public StubHandler(HttpResponseMessage response)
            : this(() => response)
        {
        }

        public StubHandler(Func<HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public StubHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);

            if (_exception is not null)
            {
                throw _exception;
            }

            return Task.FromResult(_responseFactory!());
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name)
        {
            // SEC-02 contract: the named "opencode" client is the only client the adapter
            // creates. Assert the right name is requested.
            name.Should().Be(HttpExtensions.OpenCodeClientName,
                "OpenCodeAdapter must only ever create the named 'opencode' HttpClient (so the SEC-02 + SEC-03 handlers run)");
            return new HttpClient(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri(HttpExtensions.OpenCodeBaseUrl),
                Timeout = HttpExtensions.OpenCodeTimeout,
            };
        }
    }
}
