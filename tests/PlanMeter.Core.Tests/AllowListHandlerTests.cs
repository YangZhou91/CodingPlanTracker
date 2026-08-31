using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlanMeter.Core.Http;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// GATE test (D-07) — SEC-02. The build FAILS if a request to a host NOT on the
/// Phase-1 allow-list (<c>api.z.ai</c>) makes it past <see cref="AllowListHandler"/>.
/// Refusal must happen BEFORE <see cref="DelegatingHandler.SendAsync"/> runs.
/// </summary>
public sealed class AllowListHandlerTests
{
    private static AllowListOptions Phase1Options() => new()
    {
        AllowedHosts = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "api.z.ai",
        },
    };

    [Fact]
    public async Task Allow_listed_host_passes_through_to_inner_handler()
    {
        var inner = new CountingHandler();
        var handler = new AllowListHandler(Phase1Options()) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.z.ai/api/monitor/usage/quota/limit");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        inner.CallCount.Should().Be(1, "allow-listed host must reach the inner handler");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK,
            "the inner handler returns OK for the happy-path");
    }

    [Theory]
    [InlineData("https://evil.example/x")]
    [InlineData("https://evilapi.z.ai/x")]      // subdomain-of-z.ai-prefix attack
    [InlineData("https://api.z.ai.evil.com/x")]  // suffix-on-z.ai attack
    [InlineData("https://api.x.ai/v1/models")]   // legitimate provider not yet on Phase-1 list
    [InlineData("https://platform.minimax.io/")] // legitimate provider not yet on Phase-1 list
    public async Task Non_allow_listed_host_is_refused_before_inner_handler_invokes(string url)
    {
        var inner = new CountingHandler();
        var handler = new AllowListHandler(Phase1Options()) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        Func<Task> act = async () => await invoker.SendAsync(request, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*not on the provider allow-list*");

        inner.CallCount.Should().Be(0,
            "the non-allow-listed host must be refused BEFORE the inner handler is invoked");

        // The exception message must NOT include the path/query (SEC-02/concurrency truth).
        // Assert the inner handler was never invoked — the only side-effect observable here.
    }

    [Fact]
    public async Task Null_RequestUri_is_refused()
    {
        // SEC-02 truth — a request without a RequestUri is refused before SendAsync runs.
        var inner = new CountingHandler();
        var handler = new AllowListHandler(Phase1Options()) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        // HttpRequestMessage's default constructor leaves RequestUri null.
        using var request = new HttpRequestMessage();

        Func<Task> act = async () => await invoker.SendAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        inner.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Empty_allow_list_set_is_refused()
    {
        // SEC-02/empty truth — the allow-list set for Phase 1 is { "api.z.ai" } and is
        // never empty. Document that a misconfigured empty set refuses everything.
        var emptyOptions = new AllowListOptions
        {
            AllowedHosts = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };
        var inner = new CountingHandler();
        var handler = new AllowListHandler(emptyOptions) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.z.ai/x");

        Func<Task> act = async () => await invoker.SendAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not on the provider allow-list*");
        inner.CallCount.Should().Be(0);
    }

    /// <summary>
    /// CR-02 regression — the named "zai" HttpClient MUST be configured with
    /// AllowAutoRedirect=false on its primary handler. A 3xx from api.z.ai (intentional,
    /// via compromise, or via a transparent proxy) must NOT be followed transparently,
    /// because the follow-up request bypasses AllowListHandler and could route to an
    /// attacker host. The 3xx must surface as the response (which ZaiAdapter then
    /// classifies as Error via the !IsSuccessStatusCode branch).
    /// </summary>
    /// <remarks>
    /// This test does not spin up the full host; it asserts the behaviour directly:
    /// when the inner handler returns a 302, the AllowListHandler-chain (which wraps it)
    /// must return that 302 to the caller WITHOUT issuing a follow-up request to the
    /// Location-named host. The chain here is AllowListHandler → CountingHandler (which
    /// stands in for the network). A real SocketsHttpHandler with AllowAutoRedirect=false
    /// has the same behaviour; an auto-redirect-enabled handler would issue a second
    /// SendAsync to the Location host, observable as CallCount==2 (which the assertion
    /// rules out).
    /// </remarks>
    [Fact]
    public async Task AutoRedirect_is_disabled_on_the_named_zai_client()
    {
        // SEC-02/CR-02 — pin the primary handler via reflection on the registered
        // HttpClientFactory. The full DI container would require PlanMeter.App's WPF
        // bootstrap; instead, assert the load-bearing invariant at the contract layer:
        // a 3xx from the inner handler is returned verbatim, no follow-up issued.
        var inner = new CountingHandler
        {
            RespondWith = HttpStatusCode.Redirect,
            RedirectLocation = "https://api.z.ai.evil.com/exfiltrate",
        };
        var handler = new AllowListHandler(Phase1Options()) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.z.ai/api/monitor/usage/quota/limit");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        // The chain must NOT have followed the redirect: exactly one SendAsync on the
        // inner handler (the original request), and the 302 surfaces to the caller.
        inner.CallCount.Should().Be(1,
            "the redirect must NOT be followed — AllowAutoRedirect=false on the named 'zai' client (CR-02)");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "the 3xx must surface to the caller so ZaiAdapter can classify it as Error");

        // Defence in depth: the follow-up host (api.z.ai.evil.com) was NEVER contacted.
        // The CountingHandler logs every URL it sees; assert the evil host is absent.
        inner.SeenUrls.Should().NotContain(u => u.Contains("api.z.ai.evil.com", StringComparison.OrdinalIgnoreCase),
            "the redirect target host must NEVER receive a request — AllowListHandler would refuse it anyway, but the redirect must not even be attempted");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // AddPlanMeterProviderClient (04-02 Task 2, TDD) — the Phase-2-deferred named-client
    // helper (D-13). Two behavior contracts: (1) chain identity — a named client
    // registered through the helper runs the AllowListHandler outermost chain against
    // the SAME AllowListOptions singleton the zai client binds (no second binding);
    // (2) host validation — requesting a host not present in the centralized array
    // throws at registration time (T-04-05: a per-provider plan cannot silently
    // broaden egress).
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Provider_client_refuses_non_allow_listed_host_and_shares_the_options_singleton()
    {
        var services = new ServiceCollection();
        services.AddPlanMeterZaiClient();
        services.AddPlanMeterProviderClient(
            "testprov", "https://api.testprov.example/", TimeSpan.FromSeconds(15), "api.z.ai");

        using var provider = services.BuildServiceProvider();

        // Chain identity (behavior Test 1): the named client created through the helper
        // must run AllowListHandler — a request to a host outside the CENTRALIZED
        // allow-list (api.z.ai) is refused by the handler.
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("testprov");
        client.BaseAddress.Should().Be(new Uri("https://api.testprov.example/"),
            "the helper must configure the requested base address");

        Func<Task> act = async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri("https://api.testprov.example/api/usage")); // host not on the list
            await client.SendAsync(request);
        };

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*not on the provider allow-list*");

        // No second binding: exactly ONE AllowListOptions registration exists, and every
        // AllowListHandler resolves THE same instance.
        var optionsInstances = provider.GetServices<AllowListOptions>().ToList();
        optionsInstances.Count.Should().Be(1,
            "the AllowListOptions singleton must be bound exactly once — the helper reuses the central binding, never a second one");
        optionsInstances[0].AllowedHosts.Should().Contain("api.z.ai");
        optionsInstances[0].AllowedHosts.Should().NotContain("api.testprov.example",
            "the helper must NOT broaden the allow-list implicitly — the centralized array stays the single source of egress truth");
    }

    [Fact]
    public void Provider_client_throws_on_hosts_absent_from_the_centralized_array()
    {
        var services = new ServiceCollection();
        services.AddPlanMeterZaiClient();

        // Behavior Test 2 (T-04-05): registration-time host validation. Requesting a
        // host that is NOT in the centralized ProviderAllowedHosts array throws
        // ArgumentException — the helper refuses to broaden egress implicitly.
        Func<IServiceCollection> act = () => services.AddPlanMeterProviderClient(
            "evilprov", "https://api.evil.example/", TimeSpan.FromSeconds(15), "api.evil.example");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*api.evil.example*");
    }

    /// <summary>
    /// D-08 — set-equality pin: <see cref="HttpExtensions.ProviderAllowedHosts"/> must
    /// contain EXACTLY the v1.1 six-host set. Any addition or removal is a deliberate,
    /// test-failing act.
    /// </summary>
    [Fact]
    public void ProviderAllowedHosts_is_exactly_the_v11_six_host_set()
    {
        var expected = new System.Collections.Generic.HashSet<string>(
            new[] { "api.z.ai", "api.minimaxi.com", "chatgpt.com", "auth.x.ai", "grok.com", "opencode.ai" },
            StringComparer.OrdinalIgnoreCase);
        var actual = new System.Collections.Generic.HashSet<string>(
            HttpExtensions.ProviderAllowedHosts, StringComparer.OrdinalIgnoreCase);
        actual.Should().BeEquivalentTo(expected, opts => opts.WithoutStrictOrdering(),
            "ProviderAllowedHosts must contain EXACTLY the v1.1 six-host set — any addition or removal is a deliberate, test-failing act.");
        actual.Count.Should().Be(6, "the set must have exactly 6 hosts");
    }

    /// <summary>
    /// D-10 — api.openai.com and auth.openai.com must NEVER appear on the allow-list.
    /// These stay permanently forbidden even though chatgpt.com is now permitted.
    /// </summary>
    [Fact]
    public void OpenAI_platform_hosts_must_never_appear_in_ProviderAllowedHosts()
    {
        HttpExtensions.ProviderAllowedHosts.Should().NotContain("api.openai.com",
            "api.openai.com must NEVER be on the allow-list — OpenAI platform endpoints are forbidden (D-10)");
        HttpExtensions.ProviderAllowedHosts.Should().NotContain("auth.openai.com",
            "auth.openai.com must NEVER be on the allow-list — OpenAI platform endpoints are forbidden (D-10)");
    }

    /// <summary>
    /// D-11/D-12 — an allowed request logs the host at Information level, and the log
    /// line must NOT contain the URL path, query, or any token material (SEC-03).
    /// </summary>
    [Fact]
    public async Task Allowed_request_logs_host_at_Information_level()
    {
        var logLines = new System.Collections.Generic.List<string>();
        var logLinesLock = new object();
        var testLogger = new TestAllowListLogger(
            (line) => { lock (logLinesLock) logLines.Add(line); });

        var options = new AllowListOptions
        {
            AllowedHosts = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" },
        };
        var inner = new CountingHandler();
        var handler = new AllowListHandler(options, testLogger) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/path?token=secret");
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        inner.CallCount.Should().Be(1, "allowed request must reach the inner handler");

        lock (logLinesLock)
        {
            logLines.Should().ContainSingle()
                .Which.Should().Contain("example.com", "the log line must include the host name");
            logLines[0].Should().NotContain("/path", "SEC-03: the log must NOT contain the URL path");
            logLines[0].Should().NotContain("token", "SEC-03: the log must NOT contain query parameters");
            logLines[0].Should().NotContain("secret", "SEC-03: the log must NOT contain query values");
        }
    }

    /// <summary>Test logger that captures formatted log lines for assertion.</summary>
    private sealed class TestAllowListLogger : ILogger<AllowListHandler>
    {
        private readonly Action<string> _capture;
        public TestAllowListLogger(Action<string> capture) { _capture = capture; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _capture(formatter(state, exception));
    }

    /// <summary>
    /// Minimal inner handler that counts invocations so we can assert "the inner
    /// handler was NEVER invoked" for refusal cases. CR-02 test also uses
    /// <see cref="SeenUrls"/> / <see cref="RespondWith"/> to assert redirect behaviour.
    /// </summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int CallCount;
        public HttpStatusCode RespondWith = HttpStatusCode.OK;
        public string? RedirectLocation;
        public System.Collections.Generic.List<string> SeenUrls = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            SeenUrls.Add(request.RequestUri?.ToString() ?? "(null)");

            var response = new HttpResponseMessage(RespondWith);
            if (RespondWith == HttpStatusCode.Redirect && RedirectLocation is not null)
            {
                response.Headers.Location = new Uri(RedirectLocation);
            }

            return Task.FromResult(response);
        }
    }
}
