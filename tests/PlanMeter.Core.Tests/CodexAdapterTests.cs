using System;
using System.IO;
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
/// CODEX-02 (09-01) — Codex live ManualOnly session adapter tests against an in-process
/// mock <see cref="HttpMessageHandler"/>. No network. Status-tree overflow (401/403/429/5xx)
/// lands in the next task; this file covers port conformance, Detect (file-only, D-13),
/// happy fetch + headers, Account-Id when present, and the null-token short-circuit.
/// </summary>
public sealed class CodexAdapterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Func<string, bool> _originalFileExists;
    private readonly string? _originalOpenAiApiKey;
    private readonly string? _originalCodexHome;

    private const string HappyBody = /*lang=json,strict*/ """
    {
      "rate_limit": {
        "primary_window": {
          "used_percent": 61,
          "limit_window_seconds": 604800,
          "reset_at": "2026-08-26T00:00:00Z"
        }
      }
    }
    """;

    public CodexAdapterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"codex-adapter-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _originalFileExists = CodexAdapter.FileExistsPredicate;
        _originalOpenAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        _originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");

        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        Environment.SetEnvironmentVariable("CODEX_HOME", null);
    }

    public void Dispose()
    {
        CodexAdapter.FileExistsPredicate = _originalFileExists;
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", _originalOpenAiApiKey);
        Environment.SetEnvironmentVariable("CODEX_HOME", _originalCodexHome);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private static HttpResponseMessage OkJsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private static CodexAdapter CreateAdapter(HttpMessageHandler handler, CodexAuthJsonSource? auth = null)
        => new(new StubFactory(handler), auth);

    [Fact]
    public void Port_conformance_manifest_values()
    {
        var adapter = CreateAdapter(new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)));

        adapter.Id.Should().Be(new ProviderId("codex"));
        adapter.DisplayName.Should().Be("Codex");
        adapter.SupportsUsageApi.Should().BeTrue(
            "CODEX-02 flips the Unsupported row to a live usage adapter — the poller must construct for it");
        adapter.ManualOnlyFetch.Should().BeFalse(
            "REFRESH-04 — Codex is timer-polled on the shared ~10-min cycle "
            + "(user-directed reversal of GRND-03; risk accepted)");
        adapter.RequiresManualKey.Should().BeFalse("session family — no key card");
        adapter.SupportsOAuthLogin.Should().BeFalse("Codex uses the CLI session, not PlanMeter OAuth");
        adapter.AuthFamily.Should().Be(AuthFamily.Session);
        adapter.ConsoleUrl.Should().Be("https://chatgpt.com/codex");
        adapter.QualifierText.Should().BeNull("D-12: never render ESTIMATED on Codex");
        adapter.UnsupportedReason.Should().BeNull("the row is no longer Unsupported");
        adapter.FloorReason.Should().BeNull();
        adapter.ReLoginGuidance.Should().Contain("Codex CLI").And.Contain("restart",
            "D-15: after session park, CLI re-login is not enough — PlanMeter must restart");
    }

    [Fact]
    public void Named_client_registration_does_not_grow_allow_list()
    {
        HttpExtensions.CodexClientName.Should().Be("codex");
        HttpExtensions.CodexBaseUrl.Should().Be("https://chatgpt.com/");
        HttpExtensions.CodexUsagePath.Should().Be("backend-api/wham/usage");
        HttpExtensions.CodexTimeout.Should().Be(TimeSpan.FromSeconds(15));
        HttpExtensions.ProviderAllowedHosts.Should().HaveCount(6,
            "AddPlanMeterCodexClient must not grow the six-host set");
        HttpExtensions.ProviderAllowedHosts.Should().Contain("chatgpt.com");
    }

    [Fact]
    public void Detect_returns_true_when_auth_json_exists()
    {
        string fakePath = Path.Combine(_tempDir, ".codex", "auth.json");
        CodexAdapter.FileExistsPredicate = path => path == fakePath;

        var adapter = new CodexAdapter(fakePath);
        adapter.Detect().Should().BeTrue(
            "Detect() must return true when the Codex auth.json path exists");
    }

    [Fact]
    public void Detect_returns_false_when_only_environment_api_key_is_set()
    {
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-test-key");
            CodexAdapter.FileExistsPredicate = _ => false;

            var adapter = new CodexAdapter();
            adapter.Detect().Should().BeFalse(
                "D-13: Detect is file-path presence only — an environment API key is not a Codex session");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        }
    }

    [Fact]
    public void Detect_returns_false_when_auth_json_is_absent()
    {
        CodexAdapter.FileExistsPredicate = _ => false;
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);

        var adapter = new CodexAdapter();
        adapter.Detect().Should().BeFalse(
            "Detect() must return false when the auth.json path does not exist");
    }

    [Fact]
    public void Detect_returns_true_when_file_exists_even_if_env_key_is_also_set()
    {
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-test-key");
            string fakePath = Path.Combine(_tempDir, ".codex", "auth.json");
            CodexAdapter.FileExistsPredicate = path => path == fakePath;

            var adapter = new CodexAdapter(fakePath);
            adapter.Detect().Should().BeTrue(
                "file presence is sufficient regardless of environment key");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        }
    }

    [Fact]
    public void Detect_probes_DefaultPath()
    {
        string? probedPath = null;
        CodexAdapter.FileExistsPredicate = path =>
        {
            probedPath = path;
            return false;
        };

        var adapter = new CodexAdapter();
        adapter.Detect();

        probedPath.Should().Be(CodexAuthJsonSource.DefaultPath,
            "Detect() must probe the documented ~/.codex/auth.json default path");
    }

    [Fact]
    public void Detect_probes_CODEX_HOME_override_when_set()
    {
        try
        {
            string codexHomePath = Path.Combine(_tempDir, "custom-codex-home");
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHomePath);

            string expectedPath = Path.Combine(codexHomePath, "auth.json");

            string? probedPath = null;
            CodexAdapter.FileExistsPredicate = path =>
            {
                probedPath = path;
                return false;
            };

            var adapter = new CodexAdapter();
            adapter.Detect();

            probedPath.Should().Be(expectedPath,
                "Detect() must probe {CODEX_HOME}\\auth.json when CODEX_HOME is set");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", null);
        }
    }

    [Fact]
    public async Task FetchUsageAsync_happy_weekly_issues_GET_with_Bearer_and_codex_cli_UA()
    {
        var handler = new StubHandler(() => OkJsonResponse(HappyBody));
        var adapter = CreateAdapter(handler);

        UsageReading reading = (await adapter.FetchUsageAsync("test-token", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.Ok);
        reading.UsedPct.Should().BeApproximately(61.0, 0.01);
        reading.RemainingPct.Should().BeApproximately(39.0, 0.01);
        reading.MostBindingWindow.Should().Be(WindowKind.Weekly);
        reading.ErrorMessage.Should().BeNull();

        handler.CallCount.Should().Be(1);
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Get);
        handler.LastRequest.RequestUri!.ToString().Should().Contain(HttpExtensions.CodexUsagePath);
        handler.LastRequest.Headers.Authorization.Should().NotBeNull();
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be("test-token");
        string ua = handler.LastRequest.Headers.UserAgent.ToString();
        if (string.IsNullOrEmpty(ua))
        {
            handler.LastRequest.Headers.TryGetValues("User-Agent", out var values);
            ua = values is null ? string.Empty : string.Join(" ", values);
        }
        ua.Should().Contain("codex-cli");
    }

    [Fact]
    public async Task FetchUsageAsync_sends_Account_Id_when_injected_source_has_tokens_account_id()
    {
        string authPath = Path.Combine(_tempDir, "nested-auth.json");
        File.WriteAllText(authPath, /*lang=json,strict*/ """
        {
          "tokens": {
            "access_token": "nested-abc",
            "account_id": "00000000-0000-0000-0000-000000000001"
          }
        }
        """);
        var handler = new StubHandler(() => OkJsonResponse(HappyBody));
        var adapter = CreateAdapter(handler, new CodexAuthJsonSource(authPath));

        _ = await adapter.FetchUsageAsync("test-token", CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.TryGetValues("ChatGPT-Account-Id", out var values).Should().BeTrue();
        values.Should().ContainSingle().Which.Should().Be("00000000-0000-0000-0000-000000000001");
    }

    [Fact]
    public async Task Null_or_whitespace_token_returns_NotLoggedIn_without_invoking_handler()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var adapter = CreateAdapter(handler);

        UsageReading nullKey = (await adapter.FetchUsageAsync(null, CancellationToken.None)).Reading;
        UsageReading emptyKey = (await adapter.FetchUsageAsync("  ", CancellationToken.None)).Reading;

        nullKey.Status.Should().Be(ReadingStatus.NotLoggedIn);
        nullKey.UsedPct.Should().BeNull();
        nullKey.ErrorMessage.Should().Be("Codex rejected the session.");
        emptyKey.Status.Should().Be(ReadingStatus.NotLoggedIn);
        handler.CallCount.Should().Be(0,
            "a missing token must short-circuit before any HTTP request is issued");
    }

    [Fact]
    public async Task Unauthorized_401_returns_NotLoggedIn()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var adapter = CreateAdapter(handler);

        UsageReading reading = (await adapter.FetchUsageAsync("test-token", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.NotLoggedIn);
        reading.UsedPct.Should().BeNull();
        reading.ErrorMessage.Should().Be("Codex rejected the session.");
        handler.CallCount.Should().Be(1, "401 must NOT trigger a retry — exactly one request went out");
    }

    [Fact]
    public async Task Forbidden_403_returns_NotLoggedIn()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.Forbidden));
        var adapter = CreateAdapter(handler);

        UsageReading reading = (await adapter.FetchUsageAsync("test-token", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.NotLoggedIn);
        reading.ErrorMessage.Should().Be("Codex rejected the session.");
    }

    [Fact]
    public async Task TooManyRequests_429_returns_Error_and_carries_RetryAfter()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(600)) },
        };
        var handler = new StubHandler(response);
        var adapter = CreateAdapter(handler);

        AdapterFetchResult result = await adapter.FetchUsageAsync("test-token", CancellationToken.None);

        result.Reading.Status.Should().Be(ReadingStatus.Error);
        result.Reading.ErrorMessage.Should().Contain("unavailable");
        result.RetryAfter.Should().Be(TimeSpan.FromMinutes(10),
            "the 429 Retry-After header must be carried on AdapterFetchResult");
        handler.CallCount.Should().Be(1, "429 must NOT trigger an in-tick retry");
    }

    [Fact]
    public async Task Server_error_5xx_returns_Error_without_RetryAfter()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var adapter = CreateAdapter(handler);

        AdapterFetchResult result = await adapter.FetchUsageAsync("test-token", CancellationToken.None);

        result.Reading.Status.Should().Be(ReadingStatus.Error);
        result.Reading.ErrorMessage.Should().Contain("unavailable");
        result.RetryAfter.Should().BeNull("only 429 carries the RetryAfter carrier");
    }

    [Fact]
    public async Task Network_failure_returns_Error()
    {
        var handler = new StubHandler(new HttpRequestException("simulated network down"));
        var adapter = CreateAdapter(handler);

        UsageReading reading = (await adapter.FetchUsageAsync("test-token", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.Error);
        reading.ErrorMessage.Should().Contain("Couldn't reach Codex.");
        reading.UsedPct.Should().BeNull();
    }

    [Fact]
    public async Task Malformed_json_on_200_returns_Error()
    {
        var handler = new StubHandler(() => OkJsonResponse("{rate_limit:{primary_window:{used_percent:\"not-a-number\"}}"));
        var adapter = CreateAdapter(handler);

        UsageReading reading = (await adapter.FetchUsageAsync("test-token", CancellationToken.None)).Reading;

        reading.Status.Should().Be(ReadingStatus.Error);
        reading.ErrorMessage.Should().Contain("malformed");
        reading.UsedPct.Should().BeNull();
    }

    [Fact]
    public async Task FetchUsageAsync_omits_Account_Id_when_source_has_no_account_id()
    {
        string authPath = Path.Combine(_tempDir, "flat-auth.json");
        File.WriteAllText(authPath, /*lang=json,strict*/ """
        {
          "tokens": {
            "access_token": "nested-abc"
          }
        }
        """);
        var handler = new StubHandler(() => OkJsonResponse(HappyBody));
        var adapter = CreateAdapter(handler, new CodexAuthJsonSource(authPath));

        _ = await adapter.FetchUsageAsync("test-token", CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.Contains("ChatGPT-Account-Id").Should().BeFalse(
            "D-16: omit ChatGPT-Account-Id when tokens.account_id is missing");
    }

    [Fact]
    public async Task TestFetchAsync_shares_implementation_with_FetchUsageAsync()
    {
        var testAdapter = CreateAdapter(new StubHandler(() => OkJsonResponse(HappyBody)));
        var pollAdapter = CreateAdapter(new StubHandler(() => OkJsonResponse(HappyBody)));

        UsageReading testReading = await testAdapter.TestFetchAsync("candidate-token", CancellationToken.None);
        UsageReading pollReading = (await pollAdapter.FetchUsageAsync("candidate-token", CancellationToken.None)).Reading;

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
        var adapter = CreateAdapter(handler);

        adapter.FetchCount.Should().Be(0);
        await adapter.FetchUsageAsync("test-token", CancellationToken.None);
        await adapter.TestFetchAsync("test-token", CancellationToken.None);

        adapter.FetchCount.Should().Be(2, "each FetchUsageAsync / TestFetchAsync increments the seam exactly once");
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
        public HttpRequestMessage? LastRequest;

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
            LastRequest = request;

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
            name.Should().Be(HttpExtensions.CodexClientName,
                "CodexAdapter must only ever create the named 'codex' HttpClient (so the SEC-02 + SEC-03 handlers run)");
            return new HttpClient(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri(HttpExtensions.CodexBaseUrl),
                Timeout = HttpExtensions.CodexTimeout,
            };
        }
    }
}
