using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
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
/// GROK-02/GROK-04 — the live Grok adapter suite (Unsupported pins replaced).
/// Covers: manifest flip, blob-only Detect (D-07 teardown), the reference
/// billing request shape, the Pattern-5 classification ladder, the exactly-one
/// refresh+retry budget, and the DI wiring gates (sessionSources slot + named
/// clients) via source-text assertions.
/// </summary>
public sealed class GrokAdapterTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly Func<bool> _originalBlobExists;
    private readonly string? _originalXaiApiKey;

    public GrokAdapterTests()
    {
        _originalBlobExists = GrokAdapter.BlobExistsPredicate;
        _originalXaiApiKey = Environment.GetEnvironmentVariable("XAI_API_KEY");
        Environment.SetEnvironmentVariable("XAI_API_KEY", null);
    }

    public void Dispose()
    {
        GrokAdapter.BlobExistsPredicate = _originalBlobExists;
        Environment.SetEnvironmentVariable("XAI_API_KEY", _originalXaiApiKey);
    }

    // ---- stub infrastructure ----

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        IReadOnlyDictionary<string, string> Headers,
        string? ContentType,
        byte[] Body);

    private sealed class QueuedBillingHandler : HttpMessageHandler
    {
        public sealed record StubResponse(
            HttpStatusCode Status,
            byte[]? Body,
            string ContentType,
            IReadOnlyDictionary<string, string>? Headers = null);

        private readonly Queue<StubResponse> _responses;
        private StubResponse _last = new(HttpStatusCode.OK, Array.Empty<byte>(), "application/grpc-web+proto");

        public List<RecordedRequest> Recorded { get; } = new();

        public QueuedBillingHandler(params StubResponse[] responses)
        {
            _responses = new Queue<StubResponse>(responses);
        }

        public int RequestCount => Recorded.Count;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }

            byte[] body = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(ct);
            string? contentType = request.Content?.Headers.ContentType?.MediaType;
            Recorded.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, contentType, body));

            if (_responses.Count > 0)
            {
                _last = _responses.Dequeue();
            }

            var response = new HttpResponseMessage(_last.Status)
            {
                Content = new ByteArrayContent(_last.Body ?? Array.Empty<byte>()),
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", _last.ContentType);
            foreach (var (name, value) in _last.Headers ?? new Dictionary<string, string>())
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            if (Recorded.Count == 1 && _throwOnFirst)
            {
                throw _firstException!;
            }

            return response;
        }

        private bool _throwOnFirst;
        private Exception? _firstException;

        public void ThrowOnFirstRequest(Exception exception)
        {
            _throwOnFirst = true;
            _firstException = exception;
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class StubTokens : IGrokTokenAccess
    {
        private readonly string? _valid;
        private readonly string? _forced;

        public StubTokens(string? valid = "valid-token", string? forced = "refreshed-token")
        {
            _valid = valid;
            _forced = forced;
        }

        public int ForceRefreshCalls { get; private set; }

        public Task<string?> GetValidTokenAsync(CancellationToken ct = default) => Task.FromResult(_valid);

        public Task<string?> ForceRefreshAsync(CancellationToken ct = default)
        {
            ForceRefreshCalls++;
            return Task.FromResult(_forced);
        }
    }

    private static GrokAdapter CreateAdapter(QueuedBillingHandler handler, StubTokens? tokens = null)
        => new(new StubFactory(handler), tokens ?? new StubTokens());

    private static byte[] Frame(byte[] protobuf)
        => GrokWireParser.GrpcWebFrame(protobuf);

    private static byte[] HappyBody(float percent, long? resetUnix = null)
    {
        byte[] inner = resetUnix is { } reset
            ? Concat(GrokWireParser.FieldFloat(1, percent), GrokWireParser.FieldMessage(5, GrokWireParser.FieldVarint(1, (ulong)reset)))
            : GrokWireParser.FieldFloat(1, percent);
        return Frame(GrokWireParser.FieldMessage(1, inner));
    }

    private static byte[] TrailerFrame(string text)
    {
        byte[] payload = Encoding.ASCII.GetBytes(text);
        byte[] frame = new byte[5 + payload.Length];
        frame[0] = 0x80; // trailer flags
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)payload.Length);
        payload.AsSpan().CopyTo(frame.AsSpan(5));
        return frame;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        using var ms = new MemoryStream();
        foreach (var part in parts)
        {
            ms.Write(part, 0, part.Length);
        }

        return ms.ToArray();
    }

    private static QueuedBillingHandler.StubResponse Ok(byte[] body, IReadOnlyDictionary<string, string>? headers = null)
        => new(HttpStatusCode.OK, body, "application/grpc-web+proto", headers);

    // ---- manifest (D-06/D-14 flip) ----

    [Fact]
    public void Port_conformance_manifest_values()
    {
        var adapter = new GrokAdapter();

        adapter.Id.Should().Be(new ProviderId("grok"));
        adapter.DisplayName.Should().Be("Grok");
        adapter.SupportsUsageApi.Should().BeTrue("the live billing path is proven (D-10)");
        adapter.SupportsOAuthLogin.Should().BeTrue("the settings login card ships with the live adapter");
        adapter.ManualOnlyFetch.Should().BeFalse("Grok is timer-polled by design");
        adapter.RequiresManualKey.Should().BeFalse("session family — no key card");
        adapter.AuthFamily.Should().Be(AuthFamily.Session);
        adapter.ConsoleUrl.Should().Be("https://grok.com");
        adapter.QualifierText.Should().BeNull("UIR-03 — complete removal of the estimated qualifier from row, tooltip, and automation suffix");
        adapter.UnsupportedReason.Should().BeNull("the row is live, not Unsupported");
        adapter.FloorReason.Should().BeNull();
        adapter.ReLoginGuidance.Should().Contain("Settings",
            "D-06 — re-login points at PlanMeter's own settings, not the CLI");
    }

    // ---- Detect: blob existence only (D-07) ----

    [Fact]
    public void Detect_is_blob_existence_only()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"grok-detect-{Guid.NewGuid():N}");
        try
        {
            // A real file on disk at the legacy CLI path and a set env var must
            // change NOTHING — Detect is DPAPI-blob existence ONLY.
            Directory.CreateDirectory(Path.Combine(tempDir, ".grok"));
            File.WriteAllText(Path.Combine(tempDir, ".grok", "auth.json"), "{}");
            Environment.SetEnvironmentVariable("XAI_API_KEY", "some-env-key");

            GrokAdapter.BlobExistsPredicate = () => true;
            new GrokAdapter().Detect().Should().BeTrue("blob exists -> detected");

            GrokAdapter.BlobExistsPredicate = () => false;
            new GrokAdapter().Detect().Should().BeFalse(
                "no blob -> not detected, regardless of the CLI file or env var");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
            Environment.SetEnvironmentVariable("XAI_API_KEY", null);
        }
    }

    // ---- fetch: pre-login + request shape ----

    [Fact]
    public async Task Null_or_whitespace_key_returns_NotLoggedIn_without_HTTP()
    {
        var handler = new QueuedBillingHandler();
        var adapter = CreateAdapter(handler);

        foreach (string? key in new string?[] { null, "", "   " })
        {
            var result = await adapter.FetchUsageAsync(key, CancellationToken.None);
            result.Reading.Status.Should().Be(ReadingStatus.NotLoggedIn);
        }

        handler.RequestCount.Should().Be(0, "pre-login rows never touch the network");
    }

    [Fact]
    public async Task GetValidToken_null_short_circuits_to_NotLoggedIn_without_HTTP()
    {
        var handler = new QueuedBillingHandler();
        var adapter = CreateAdapter(handler, new StubTokens(valid: null));

        var result = await adapter.FetchUsageAsync("stale-peeked-token", CancellationToken.None);

        result.Reading.Status.Should().Be(ReadingStatus.NotLoggedIn,
            "a ReAuthRequired manager parks the fetch before any HTTP");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task Billing_request_uses_reference_header_set_and_five_byte_empty_frame()
    {
        var handler = new QueuedBillingHandler(Ok(HappyBody(47f, Now.AddDays(30).ToUnixTimeSeconds())));
        var adapter = CreateAdapter(handler);

        await adapter.FetchUsageAsync("peeked-token", CancellationToken.None);

        handler.RequestCount.Should().Be(1);
        RecordedRequest request = handler.Recorded[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.AbsolutePath.Should().Be("/" + HttpExtensions.GrokBillingPath);
        request.Headers["Authorization"].Should().Be("Bearer valid-token");
        request.Headers["Origin"].Should().Be("https://grok.com");
        request.Headers["Referer"].Should().Be("https://grok.com/?_s=usage");
        request.Headers["Accept"].Should().Be("*/*");
        request.Headers["x-grpc-web"].Should().Be("1");
        request.Headers["x-user-agent"].Should().Be("connect-es/2.1.1");
        request.ContentType.Should().Be("application/grpc-web+proto");
        request.Body.Should().HaveCount(5).And.OnlyContain(b => b == 0,
            "the empty request message is the true 5-byte zero frame");
    }

    // ---- fetch: happy path ----

    [Fact]
    public async Task Happy_path_200_frame_returns_Ok_with_figure_and_banded_window()
    {
        long reset = Now.AddDays(30).ToUnixTimeSeconds();
        var handler = new QueuedBillingHandler(Ok(HappyBody(47f, reset)));
        var adapter = CreateAdapter(handler);

        var result = await adapter.FetchUsageAsync("peeked-token", CancellationToken.None);

        result.Reading.Status.Should().Be(ReadingStatus.Ok);
        result.Reading.UsedPct.Should().Be(47f);
        result.Reading.RemainingPct.Should().Be(53f);
        result.Reading.MostBindingWindow.Should().Be(WindowKind.Monthly, "30 days to reset falls in the monthly band");
        result.Reading.AllWindows.Should().ContainSingle()
            .Which.ResetsAtUtc.Should().NotBeNull();
        result.RetryAfter.Should().BeNull();
    }

    [Fact]
    public async Task Happy_path_without_reset_maps_to_Credits_window()
    {
        var handler = new QueuedBillingHandler(Ok(HappyBody(47f)));
        var adapter = CreateAdapter(handler);

        var result = await adapter.FetchUsageAsync("peeked-token", CancellationToken.None);

        result.Reading.Status.Should().Be(ReadingStatus.Ok);
        result.Reading.MostBindingWindow.Should().Be(WindowKind.Credits,
            "no reset reported -> the credits bucket (CRED chip)");
    }

    // ---- fetch: auth failures and the one-refresh-one-retry budget ----

    [Fact]
    public async Task HTTP_401_refreshes_once_then_retries_once_then_NotLoggedIn()
    {
        var handler = new QueuedBillingHandler(
            new QueuedBillingHandler.StubResponse(HttpStatusCode.Unauthorized, Array.Empty<byte>(), "text/html"),
            new QueuedBillingHandler.StubResponse(HttpStatusCode.Unauthorized, Array.Empty<byte>(), "text/html"));
        var tokens = new StubTokens();
        var adapter = CreateAdapter(handler, tokens);

        var result = await adapter.FetchUsageAsync("peeked-token", CancellationToken.None);

        result.Reading.Status.Should().Be(ReadingStatus.NotLoggedIn);
        handler.RequestCount.Should().Be(2, "exactly one original POST + one retry");
        tokens.ForceRefreshCalls.Should().Be(1, "the refresh budget is exactly one");
    }

    [Fact]
    public async Task HTTP_401_then_successful_retry_returns_the_retry_reading()
    {
        var handler = new QueuedBillingHandler(
            new QueuedBillingHandler.StubResponse(HttpStatusCode.Unauthorized, Array.Empty<byte>(), "text/html"),
            Ok(HappyBody(12.5f)));
        var tokens = new StubTokens();
        var adapter = CreateAdapter(handler, tokens);

        var result = await adapter.FetchUsageAsync("peeked-token", CancellationToken.None);

        result.Reading.Status.Should().Be(ReadingStatus.Ok);
        result.Reading.UsedPct.Should().Be(12.5f);
        tokens.ForceRefreshCalls.Should().Be(1);
        handler.Recorded[1].Headers["Authorization"].Should().Be("Bearer refreshed-token",
            "the retry uses the refreshed token");
    }

    [Fact]
    public async Task Refresh_dead_invalid_grant_short_circuits_to_NotLoggedIn()
    {
        var handler = new QueuedBillingHandler(
            new QueuedBillingHandler.StubResponse(HttpStatusCode.Unauthorized, Array.Empty<byte>(), "text/html"));
        var tokens = new StubTokens(forced: null); // ReAuthRequired — refresh is dead
        var adapter = CreateAdapter(handler, tokens);

        var result = await adapter.FetchUsageAsync("peeked-token", CancellationToken.None);

        result.Reading.Status.Should().Be(ReadingStatus.NotLoggedIn);
        handler.RequestCount.Should().Be(1, "no retry when the refresh itself failed");
        tokens.ForceRefreshCalls.Should().Be(1);
    }

    // ---- fetch: gRPC classification (headers first, then body trailer) ----

    [Fact]
    public async Task Trailers_only_grpc_status_16_in_headers_is_auth_failure()
    {
        var headers16 = new Dictionary<string, string>
        {
            ["grpc-status"] = "16",
            ["grpc-message"] = "No%20credentials%20presented.",
        };
        var handler = new QueuedBillingHandler(
            Ok(Array.Empty<byte>(), headers16),
            Ok(Array.Empty<byte>(), headers16));
        var tokens = new StubTokens();
        var adapter = CreateAdapter(handler, tokens);

        var result = await adapter.FetchUsageAsync("peeked-token", CancellationToken.None);

        result.Reading.Status.Should().Be(ReadingStatus.NotLoggedIn,
            "grpc-status 16 is always an auth failure (trailers-only case, headers-first)");
        handler.RequestCount.Should().Be(2);
        tokens.ForceRefreshCalls.Should().Be(1);
    }

    [Fact]
    public async Task Grpc_status_9_no_personal_team_is_Error_not_NotLoggedIn()
    {
        var handler = new QueuedBillingHandler(Ok(Array.Empty<byte>(), new Dictionary<string, string>
        {
            ["grpc-status"] = "9",
            ["grpc-message"] = "no%20personal%20team",
        }));
        var tokens = new StubTokens();
        var adapter = CreateAdapter(handler, tokens);

        var result = await adapter.FetchUsageAsync("peeked-token", CancellationToken.None);

        result.Reading.Status.Should().Be(ReadingStatus.Error,
            "a valid credential on a team account is an Error, not a park");
        result.Reading.ErrorMessage.Should().NotBeNullOrEmpty();
        handler.RequestCount.Should().Be(1);
        tokens.ForceRefreshCalls.Should().Be(0);
    }

    [Fact]
    public async Task Grpc_status_7_message_split_is_auth_failure_and_unrelated_is_Error()
    {
        var badCredentials = new Dictionary<string, string>
        {
            ["grpc-status"] = "7",
            ["grpc-message"] = "Bad-Credentials%20presented",
        };

        // Matching message -> auth path (refresh + retry once; second failure parks).
        var authHandler = new QueuedBillingHandler(
            Ok(Array.Empty<byte>(), badCredentials),
            Ok(Array.Empty<byte>(), badCredentials));
        var authTokens = new StubTokens();
        var authResult = await CreateAdapter(authHandler, authTokens).FetchUsageAsync("t", CancellationToken.None);
        authResult.Reading.Status.Should().Be(ReadingStatus.NotLoggedIn);
        authTokens.ForceRefreshCalls.Should().Be(1);

        // Unrelated message -> deterministic Error, no refresh.
        var otherHandler = new QueuedBillingHandler(Ok(Array.Empty<byte>(), new Dictionary<string, string>
        {
            ["grpc-status"] = "7",
            ["grpc-message"] = "team%20not%20found",
        }));
        var otherTokens = new StubTokens();
        var otherResult = await CreateAdapter(otherHandler, otherTokens).FetchUsageAsync("t", CancellationToken.None);
        otherResult.Reading.Status.Should().Be(ReadingStatus.Error);
        otherTokens.ForceRefreshCalls.Should().Be(0);
    }

    [Fact]
    public async Task Grpc_status_in_body_trailer_frame_is_detected()
    {
        byte[] body = Concat(
            Frame(GrokWireParser.FieldMessage(1, GrokWireParser.FieldFloat(1, 50f))),
            TrailerFrame("grpc-status: 16\r\ngrpc-message: unauthenticated"));
        var handler = new QueuedBillingHandler(
            Ok(body),
            Ok(body));
        var tokens = new StubTokens();
        var adapter = CreateAdapter(handler, tokens);

        var result = await adapter.FetchUsageAsync("peeked-token", CancellationToken.None);

        result.Reading.Status.Should().Be(ReadingStatus.NotLoggedIn,
            "grpc-status in the final trailer frame is detected and classified");
        handler.RequestCount.Should().Be(2);
        tokens.ForceRefreshCalls.Should().Be(1);
    }

    [Fact]
    public async Task Nothing_plausible_yields_Error_fixed_string()
    {
        byte[] unreadable = Frame(new byte[] { 0x71, 0x72, 0x73, 0x74 });
        var handler = new QueuedBillingHandler(Ok(unreadable));
        var adapter = CreateAdapter(handler);

        var result = await adapter.FetchUsageAsync("peeked-token", CancellationToken.None);

        result.Reading.Status.Should().Be(ReadingStatus.Error);
        result.Reading.ErrorMessage.Should().Be("Grok returned data PlanMeter couldn't read.");
        result.Reading.ErrorMessage.Should().NotContainAny("qrst", "0x71",
            "the message is a fixed string — response body text is never embedded");
    }

    // ---- fetch: network + non-success HTTP ----

    [Fact]
    public async Task Network_and_timeout_return_Error()
    {
        var networkHandler = new QueuedBillingHandler();
        networkHandler.ThrowOnFirstRequest(new HttpRequestException("socket exploded"));
        var networkResult = await CreateAdapter(networkHandler).FetchUsageAsync("t", CancellationToken.None);
        networkResult.Reading.Status.Should().Be(ReadingStatus.Error);
        networkResult.Reading.ErrorMessage.Should().Be("Couldn't reach Grok.");

        var timeoutHandler = new QueuedBillingHandler();
        timeoutHandler.ThrowOnFirstRequest(new TaskCanceledException("timed out"));
        var timeoutResult = await CreateAdapter(timeoutHandler).FetchUsageAsync("t", CancellationToken.None);
        timeoutResult.Reading.Status.Should().Be(ReadingStatus.Error);
        timeoutResult.Reading.ErrorMessage.Should().Be("Couldn't reach Grok.");
    }

    [Fact]
    public async Task Other_non_success_HTTP_is_Error()
    {
        var handler = new QueuedBillingHandler(
            new QueuedBillingHandler.StubResponse(HttpStatusCode.InternalServerError, Array.Empty<byte>(), "text/html"));
        var adapter = CreateAdapter(handler);

        var result = await adapter.FetchUsageAsync("peeked-token", CancellationToken.None);

        result.Reading.Status.Should().Be(ReadingStatus.Error);
        result.Reading.ErrorMessage.Should().NotContain("<", "no HTML body text in the message");
    }

    // ---- D-07 teardown + DI wiring gates ----

    [Fact]
    public void GrokAuthJsonSource_is_gone()
    {
        string projectRoot = FindRepoRoot();
        File.Exists(Path.Combine(projectRoot, "src", "PlanMeter.Core", "Credentials", "GrokAuthJsonSource.cs"))
            .Should().BeFalse("D-07: the CLI-file source is deleted, not supplemented");
        File.Exists(Path.Combine(projectRoot, "tests", "PlanMeter.Core.Tests", "GrokAuthJsonSourceTests.cs"))
            .Should().BeFalse("its test file is deleted with it");
    }

    [Fact]
    public void SessionSources_slot_binds_GrokCredentialSource()
    {
        string projectRoot = FindRepoRoot();
        string appSource = File.ReadAllText(Path.Combine(projectRoot, "src", "PlanMeter.App", "App.xaml.cs"));

        appSource.Should().Contain("GrokCredentialSource",
            "the sessionSources[\"grok\"] slot survives with the new blob-backed source (park discriminator)");
        appSource.Should().Contain("AddPlanMeterGrokAuthClient");
        appSource.Should().Contain("AddPlanMeterGrokBillingClient");
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Repo root not found.");
    }
}
