using System;
using System.IO;
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
/// MINI-01 — MiniMax adapter tests against an in-process mock <see cref="HttpMessageHandler"/>.
/// Mirrors ZaiAdapterTests seam (StubHandler/StubFactory with CallCount). Covers the full
/// classification ladder: empty key, 401/403, 429+RetryAfter, 200-with-failure-envelope,
/// malformed JSON, network failure, and port conformance.
/// </summary>
public sealed class MinimaxAdapterTests
{
    /// <summary>
    /// Test 6: empty key -> NotLoggedIn with CallCount == 0 (zero HTTP).
    /// </summary>
    [Fact]
    public async Task Empty_key_returns_NotLoggedIn_without_invoking_handler()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var adapter = new MinimaxAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.NotLoggedIn);
        handler.CallCount.Should().Be(0,
            "an empty key must short-circuit before any HTTP request is issued");
    }

    /// <summary>
    /// Test 7: 401 -> NotLoggedIn with CallCount == 1 (exactly one request, never retried).
    /// </summary>
    [Fact]
    public async Task Unauthorized_401_returns_NotLoggedIn_with_one_request()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var adapter = new MinimaxAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.NotLoggedIn);
        reading.UsedPct.Should().BeNull();
        reading.ErrorMessage.Should().Contain("MiniMax rejected the key");
        handler.CallCount.Should().Be(1,
            "401 must NOT trigger a self-refresh / retry — exactly one request went out");
    }

    /// <summary>
    /// Test 7b: 403 -> NotLoggedIn (same as 401).
    /// </summary>
    [Fact]
    public async Task Forbidden_403_returns_NotLoggedIn()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.Forbidden));
        var adapter = new MinimaxAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.NotLoggedIn);
        handler.CallCount.Should().Be(1);
    }

    /// <summary>
    /// Test 8: 429 + RetryConditionHeaderValue -> Error with result.RetryAfter carried.
    /// </summary>
    [Fact]
    public async Task TooManyRequests_429_with_RetryAfter_returns_Error_and_carries_retry_after()
    {
        var response = new HttpResponseMessage((HttpStatusCode)429)
        {
            Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1800)) },
        };
        var handler = new StubHandler(response);
        var adapter = new MinimaxAdapter(new StubFactory(handler));

        AdapterFetchResult result = await adapter.FetchUsageAsync("test-key", CancellationToken.None);

        result.Reading.Status.Should().Be(ReadingStatus.Error);
        result.Reading.ErrorMessage.Should().Contain("MiniMax is unavailable");
        result.RetryAfter.Should().Be(TimeSpan.FromMinutes(30),
            "the 429 Retry-After header must be carried on AdapterFetchResult");
        handler.CallCount.Should().Be(1,
            "429 must NOT trigger an in-tick retry — exactly one request");
    }

    /// <summary>
    /// Test 9a: 200-with-failure-envelope (base_resp.status_code != 0, auth-class code 1004)
    /// -> classified NotLoggedIn BEFORE normalizing.
    /// </summary>
    [Fact]
    public async Task Failure_envelope_auth_class_code_1004_returns_NotLoggedIn()
    {
        string body = /*lang=json,strict*/ """
        {
          "model_remains": [],
          "base_resp": { "status_code": 1004, "status_msg": "auth insufficient" }
        }
        """;
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });
        var adapter = new MinimaxAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.NotLoggedIn,
            "auth-class failure code 1004 in base_resp -> NotLoggedIn (classified BEFORE normalizing)");
        reading.UsedPct.Should().BeNull();
        reading.ErrorMessage.Should().Contain("MiniMax rejected the key");
        handler.CallCount.Should().Be(1);
    }

    /// <summary>
    /// Test 9b: 200-with-failure-envelope (base_resp.status_code != 0, non-auth code)
    /// -> classified Error BEFORE normalizing.
    /// </summary>
    [Fact]
    public async Task Failure_envelope_non_auth_code_returns_Error()
    {
        string body = /*lang=json,strict*/ """
        {
          "model_remains": [],
          "base_resp": { "status_code": 500, "status_msg": "internal error" }
        }
        """;
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });
        var adapter = new MinimaxAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.Error,
            "non-auth failure code in base_resp -> Error (classified BEFORE normalizing)");
        reading.UsedPct.Should().BeNull();
        reading.ErrorMessage.Should().NotBeNullOrEmpty();
        handler.CallCount.Should().Be(1);
    }

    /// <summary>
    /// Test 9c: 200-with-failure-envelope (base_resp.status_code 401, auth-class)
    /// -> classified NotLoggedIn BEFORE normalizing.
    /// </summary>
    [Fact]
    public async Task Failure_envelope_auth_code_401_returns_NotLoggedIn()
    {
        string body = /*lang=json,strict*/ """
        {
          "model_remains": [],
          "base_resp": { "status_code": 401, "status_msg": "token expired" }
        }
        """;
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });
        var adapter = new MinimaxAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.NotLoggedIn,
            "auth-class code 401 in base_resp -> NotLoggedIn");
        reading.ErrorMessage.Should().Contain("MiniMax rejected the key");
    }

    /// <summary>
    /// Test 10: malformed JSON body -> Error (never throws).
    /// </summary>
    [Fact]
    public async Task Malformed_JSON_body_returns_Error()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not json}", System.Text.Encoding.UTF8, "application/json"),
        });
        var adapter = new MinimaxAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.Error);
        reading.ErrorMessage.Should().Contain("malformed");
    }

    /// <summary>
    /// Test 11: network failure (HttpRequestException from the stub) -> Error.
    /// </summary>
    [Fact]
    public async Task Network_failure_returns_Error()
    {
        var handler = new StubHandler(new HttpRequestException("simulated network down"));
        var adapter = new MinimaxAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.Error);
        reading.UsedPct.Should().BeNull();
        reading.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Test 11b: server error 5xx -> Error.
    /// </summary>
    [Fact]
    public async Task Server_error_5xx_returns_Error()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var adapter = new MinimaxAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.Error);
        reading.ErrorMessage.Should().Contain("MiniMax is unavailable");
    }

    /// <summary>
    /// Test 12: Port conformance — Id, DisplayName, SupportsUsageApi, RequiresManualKey,
    /// AuthFamily, ConsoleUrl, FloorReason null, Detect per blob existence, named client requested.
    /// </summary>
    [Fact]
    public void Port_conformance_manifest_and_detect()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var adapter = new MinimaxAdapter(new StubFactory(handler));

        adapter.Id.Should().Be(new ProviderId("minimax"));
        adapter.DisplayName.Should().Be("MiniMax");
        adapter.SupportsUsageApi.Should().BeTrue(
            "spike-confirmed: MiniMax exposes a programmatic usage endpoint");
        adapter.RequiresManualKey.Should().BeTrue(
            "MiniMax is API-key based — the settings window must render a key field (D-10)");
        adapter.AuthFamily.Should().Be(AuthFamily.Key);
        adapter.ConsoleUrl.Should().Be("https://platform.minimax.io/subscribe/token-plan");
        adapter.QualifierText.Should().BeNull();
        adapter.UnsupportedReason.Should().BeNull();
        adapter.FloorReason.Should().BeNull();
        adapter.Detect().Should().Be(
            File.Exists(PlanMeter.Core.Credentials.DpapiKeyStore.ForProvider("minimax").BlobPath),
            "Detect() is a read-only File.Exists probe of the DPAPI blob path (SEC-01)");
    }

    /// <summary>
    /// Additional: 200-with-success-body returns Ok reading with correct window data.
    /// Ensures the normalizer integration through the adapter is correct.
    /// </summary>
    [Fact]
    public async Task Success_200_with_body_returns_Ok_reading()
    {
        string body = /*lang=json,strict*/ """
        {
          "model_remains": [
            {
              "start_time": 1786932000000,
              "end_time": 1786950000000,
              "current_interval_remaining_percent": 96,
              "current_weekly_remaining_percent": 98,
              "weekly_start_time": 1786896000000,
              "weekly_end_time": 1787500800000
            }
          ],
          "base_resp": { "status_code": 0, "status_msg": "success" }
        }
        """;
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });
        var adapter = new MinimaxAdapter(new StubFactory(handler));

        UsageReading reading = (await adapter.FetchUsageAsync("test-key", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.Ok);
        reading.UsedPct.Should().BeApproximately(4.0, 0.01,
            "remaining 96 -> UsedPct = 4 (remaining semantics)");
        reading.MostBindingWindow.Should().Be(WindowKind.FiveHour);
        reading.AllWindows!.Count.Should().Be(2,
            "1 entry x 2 windows = 2");
        reading.ErrorMessage.Should().BeNull();
        handler.CallCount.Should().Be(1);
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
            name.Should().Be(HttpExtensions.MinimaxClientName,
                "MinimaxAdapter must only ever create the named 'minimax' HttpClient (so the SEC-02 + SEC-03 handlers run)");
            return new HttpClient(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri(HttpExtensions.MinimaxBaseUrl),
                Timeout = HttpExtensions.MinimaxTimeout,
            };
        }
    }
}
