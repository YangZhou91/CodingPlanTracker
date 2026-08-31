using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PlanMeter.Core.Auth;
using PlanMeter.Core.Credentials;
using PlanMeter.Core.Http;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace PlanMeter.Core.Tests;

/// <summary>
/// D-10 LIVE tracer (10-01 Task 3) — <c>[Trait("Category","Dogfood")]</c> so the default
/// suite (<c>Category!=Dogfood</c>) never runs it. This is the phase's existential gate:
/// ONE real RFC 8628 device-code login against <c>auth.x.ai</c> (the user approves in
/// their browser — the only step no agent can perform), then ONE real gRPC-web billing
/// POST from .NET against <c>grok.com</c>. PASS evidence = a <c>grpc-status</c> response
/// header or a protobuf/gRPC-web body; BLOCKED evidence = HTTP 403 + text/html Cloudflare
/// challenge or a <c>cf-mitigated</c> header → the phase STOPS (D-10).
/// </summary>
/// <remarks>
/// <para>CROSS-PROCESS RESUME: the executor relays the user_code to the human and
/// returns a checkpoint; the human approves asynchronously while this test keeps
/// polling in the background. If this process dies before approval, a re-run RESUMES
/// the still-live device flow from the status file instead of issuing a new code —
/// the abandoned-code edge (must_haves: second start issues a NEW code) is untouched:
/// a fresh start only happens when no live issued code exists.</para>
/// <para>REDACTION: the status file carries the user_code, verification_uri, and the
/// short-lived device_code (minutes-lifetime, user-profile temp dir) but NEVER the
/// access/refresh tokens — those go only into the DPAPI blob. No tokens, bodies, or
/// exception messages are printed; only fixed evidence strings.</para>
/// </remarks>
public sealed class GrokLiveTracerTests
{
    private readonly ITestOutputHelper _output;

    public GrokLiveTracerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Machine-readable relay state for the executor/continuation coordination.</summary>
    private static string StatusPath =>
        Path.Combine(Path.GetTempPath(), "planmeter-grok-tracer-status.json");

    [Fact]
    [Trait("Category", "Dogfood")]
    public async Task Live_device_login_and_grpc_web_billing_probe()
    {
        TracerStatus? prior = TryReadStatus();

        // Idempotent re-run: a completed tracer re-verifies its evidence and passes.
        if (prior is { State: "complete" })
        {
            Relay($"prior PASS evidence: HTTP {prior.HttpStatusCode}, grpc-status {prior.GrpcStatus}, body {prior.BodyLength}B, blob {prior.BlobPath}");
            VerifyBlobExists(prior);
            VerifyFixtureIfExists(prior);
            return;
        }

        // A prior terminal state that is NOT complete re-fails with its evidence
        // (blocked = the phase stop signal; denied/failed/expired are re-runnable by
        // deleting the status file — see FAILED-path note below).
        if (prior is { State: "blocked" })
        {
            Assert.Fail(
                $"D-10 BLOCKED (recorded): HTTP {prior.HttpStatusCode}, grpc-status '{prior.GrpcStatus}' — " +
                "Cloudflare challenged the .NET client. The phase STOPS here; re-discuss before 10-02/10-03.");
        }

        var services = new ServiceCollection();
        services.AddPlanMeterGrokAuthClient();
        services.AddPlanMeterGrokBillingClient();
        using ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();

        var flow = new GrokOAuthFlow(factory);
        var manager = new GrokTokenManager(factory); // REAL DPAPI store, REAL clock

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool resume = prior is { State: "issued", DeviceCode: not null }
            && now < prior.ExpiresAtUnixSeconds - 10; // 10s margin so the code is actually pollable

        DeviceFlowStart start;
        long deadlineUnix;
        int interval;
        if (resume)
        {
            start = new DeviceFlowStart
            {
                DeviceCode = prior!.DeviceCode!,
                UserCode = prior.UserCode ?? string.Empty,
                VerificationUri = prior.VerificationUri ?? string.Empty,
                TokenEndpoint = prior.TokenEndpoint ?? GrokOAuthFlow.TokenPath,
                IntervalSeconds = prior.IntervalSeconds,
                IssuedAtUtc = DateTimeOffset.FromUnixTimeSeconds(prior.IssuedAtUnixSeconds),
            };
            deadlineUnix = prior.ExpiresAtUnixSeconds;
            interval = prior.IntervalSeconds;
            Relay($"RESUMED in-flight device flow — user_code {start.UserCode} ({prior.ExpiresAtUnixSeconds - now}s left). Waiting for the browser approval…");
        }
        else
        {
            start = await flow.StartDeviceFlowAsync();
            if (!start.Success)
            {
                // A device-grant rejection here is the D-09 pivot trigger: record + fail.
                WriteStatus(new TracerStatus { State = "failed", Error = start.FailureMessage });
                Assert.Fail(
                    $"Device flow failed to start: {start.FailureMessage} — if this is a grant-type " +
                    "rejection, the pre-authorized D-09 loopback pivot applies: STOP the plan, do NOT improvise.");
            }

            // Prohibition P5 — host-validate BEFORE any relay/auto-open. (The flow already
            // validated; this is the independent check at the relay boundary. LIVE shape:
            // auth.x.ai returns https://accounts.x.ai/oauth2/device.)
            if (!Uri.TryCreate(start.VerificationUri, UriKind.Absolute, out Uri? uri)
                || !GrokOAuthFlow.IsTrustedVerificationUri(uri))
            {
                WriteStatus(new TracerStatus { State = "failed", Error = "verification_uri host mismatch" });
                Assert.Fail($"Refusing to relay verification_uri with a non-auth.x.ai host: {start.VerificationUri}");
            }

            deadlineUnix = start.IssuedAtUtc.ToUnixTimeSeconds() + start.ExpiresIn;
            interval = start.IntervalSeconds;
            WriteStatus(new TracerStatus
            {
                State = "issued",
                UserCode = start.UserCode,
                VerificationUri = start.VerificationUri,
                DeviceCode = start.DeviceCode,
                TokenEndpoint = start.TokenEndpoint,
                IntervalSeconds = interval,
                IssuedAtUnixSeconds = start.IssuedAtUtc.ToUnixTimeSeconds(),
                ExpiresAtUnixSeconds = deadlineUnix,
            });

            Relay(
                "USER ACTION REQUIRED — open the verification_uri, enter the user_code, approve with the xAI account holding the Grok subscription:",
                $"  user_code:        {start.UserCode}",
                $"  verification_uri: {start.VerificationUri}",
                $"  (poll interval {interval}s, code expires at unix {deadlineUnix})");
            TryOpenBrowser(uri);
        }

        // ------------------------------------------------------------------
        // Poll to completion (RFC 8628 cadence, wall-clock expiry deadline).
        // ------------------------------------------------------------------
        TokenSet? tokens = null;
        while (true)
        {
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= deadlineUnix)
            {
                WriteStatus(new TracerStatus { State = "expired" });
                Assert.Fail("Device code expired before approval — re-run the tracer for a fresh code.");
            }

            DeviceFlowStep step = await flow.PollTokenAsync(start);

            switch (step)
            {
                case DeviceFlowStep.Pending:
                    await Task.Delay(TimeSpan.FromSeconds(interval));
                    break;

                case DeviceFlowStep.SlowDown:
                    interval = Math.Min(
                        interval + GrokOAuthFlow.SlowDownIncrementSeconds,
                        GrokOAuthFlow.PollIntervalCapSeconds);
                    Relay($"slow_down — interval widened to {interval}s");
                    await Task.Delay(TimeSpan.FromSeconds(interval));
                    break;

                case DeviceFlowStep.Complete complete:
                    tokens = complete.Tokens;
                    goto Polled;

                case DeviceFlowStep.Expired:
                    WriteStatus(new TracerStatus { State = "expired" });
                    throw new XunitException("auth.x.ai reported expired_token — re-run the tracer for a fresh code.");

                case DeviceFlowStep.Denied:
                    WriteStatus(new TracerStatus { State = "denied" });
                    throw new XunitException("The login was denied at the approval page.");

                case DeviceFlowStep.Failed failed:
                    WriteStatus(new TracerStatus { State = "failed", Error = failed.Message });
                    throw new XunitException($"Login failed: {failed.Message}");

                default:
                    WriteStatus(new TracerStatus { State = "failed", Error = "unknown step" });
                    throw new XunitException($"Unknown poll step: {step?.GetType().Name}");
            }
        }

    Polled:
        // GROK-03 live proof: PlanMeter's own DPAPI blob now holds the real token set.
        await manager.SaveInitialTokensAsync(tokens!);
        string blobPath = DpapiKeyStore.ForProvider("grok").BlobPath;
        File.Exists(blobPath).Should().BeTrue("SaveInitialTokens persisted the real DPAPI blob (GROK-03)");
        Relay($"LOGIN COMPLETE — DPAPI blob written: {blobPath}");

        string accessToken = (await manager.GetValidTokenAsync())
            ?? throw new InvalidOperationException("GetValidTokenAsync returned null right after SaveInitialTokens — spine bug.");

        // ------------------------------------------------------------------
        // The billing probe — the exact reference request shape from .NET (D-10).
        // ------------------------------------------------------------------
        HttpClient billing = factory.CreateClient(HttpExtensions.GrokBillingClientName);

        using var request = new HttpRequestMessage(HttpMethod.Post, HttpExtensions.GrokBillingPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Origin", "https://grok.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://grok.com/?_s=usage");
        request.Headers.Accept.Clear();                 // Pitfall 8: override the client's application/json default
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.TryAddWithoutValidation("x-grpc-web", "1");
        request.Headers.TryAddWithoutValidation("x-user-agent", "connect-es/2.1.1");

        var frame = new ByteArrayContent(new byte[5]);  // the true empty frame (Pitfall 2: never from a string)
        frame.Headers.ContentType = new MediaTypeHeaderValue("application/grpc-web+proto");
        request.Content = frame;

        using HttpResponseMessage response = await billing.SendAsync(request);
        byte[] body = await response.Content.ReadAsByteArrayAsync();

        int status = (int)response.StatusCode;
        string? grpcStatus = response.Headers.TryGetValues("grpc-status", out var statuses)
            ? statuses.FirstOrDefault()
            : null;
        string grpcMessage = response.Headers.TryGetValues("grpc-message", out var messages)
            ? PercentDecode(messages.FirstOrDefault() ?? string.Empty)
            : string.Empty;
        string? contentType = response.Content.Headers.ContentType?.MediaType;

        // GATE — the D-10 stop signal: a Cloudflare challenge, NOT an app-level response.
        bool cfMitigated = response.Headers.Contains("cf-mitigated");
        bool htmlChallenge = status == 403 && contentType == "text/html";
        if (cfMitigated || htmlChallenge)
        {
            WriteStatus(new TracerStatus
            {
                State = "blocked",
                HttpStatusCode = status,
                GrpcStatus = grpcStatus,
                GrpcMessage = grpcMessage,
                BodyLength = body.Length,
                BlobPath = blobPath,
                Error = "Cloudflare challenge",
            });
            Assert.Fail(
                $"D-10 BLOCKED: HTTP {status}, content-type '{contentType}', cf-mitigated={cfMitigated}, " +
                $"{body.Length}B body — Cloudflare challenged the .NET client. STOP the phase; re-discuss (10-02/10-03 must not run).");
        }

        bool grpcHeaderPresent = grpcStatus is not null;
        bool protobufBody = LooksLikeGrpcWebOrProtobuf(body);

        // PASS evidence — any grpc-status header OR a frame/protobuf-shaped body.
        if (!grpcHeaderPresent && !protobufBody)
        {
            WriteStatus(new TracerStatus
            {
                State = "failed",
                HttpStatusCode = status,
                GrpcStatus = grpcStatus,
                BodyLength = body.Length,
                BlobPath = blobPath,
                Error = "no grpc evidence",
            });
            Assert.Fail(
                $"D-10 INCONCLUSIVE: HTTP {status}, no grpc-status header, body does not look like gRPC-web/protobuf " +
                $"({body.Length}B, content-type '{contentType}') — investigate before 10-02.");
        }

        string hexPrefix = Convert.ToHexString(body, 0, Math.Min(64, body.Length)).ToLowerInvariant();

        // Capture the real frame for 10-02's parser fixture (research Open Question 2:
        // the body contains no credentials).
        string? fixturePath = null;
        if (body.Length > 0)
        {
            string fixturesDir = Path.Combine(FindTestProjectDir(), "Fixtures");
            Directory.CreateDirectory(fixturesDir);
            fixturePath = Path.Combine(fixturesDir, "grok-credits-live.bin");
            File.WriteAllBytes(fixturePath, body);
        }

        WriteStatus(new TracerStatus
        {
            State = "complete",
            HttpStatusCode = status,
            GrpcStatus = grpcStatus,
            GrpcMessage = grpcMessage,
            BodyLength = body.Length,
            HexPrefix = hexPrefix,
            BlobPath = blobPath,
            FixturePath = fixturePath,
        });

        Relay(
            "D-10 PASS — the .NET client reached the grok.com gRPC backend:",
            $"  HTTP {status}, grpc-status '{grpcStatus}', grpc-message '{grpcMessage}'",
            $"  body {body.Length}B, first-64B hex: {hexPrefix}",
            fixturePath is null ? "  (empty body — no fixture written)" : $"  fixture: {fixturePath}",
            $"  DPAPI blob: {blobPath}");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private void Relay(params string[] lines)
    {
        foreach (string line in lines)
        {
            _output.WriteLine(line);
            Console.WriteLine(line);
        }
    }

    private static void TryOpenBrowser(Uri uri)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
            if (process is null)
            {
                Console.WriteLine("(auto-open returned null — open the URI manually)");
            }
        }
        catch (Exception)
        {
            Console.WriteLine("(auto-open failed — open the URI manually)");
        }
    }

    private static void VerifyBlobExists(TracerStatus prior)
    {
        if (prior.BlobPath is not null)
        {
            File.Exists(prior.BlobPath).Should().BeTrue("the DPAPI blob from the live login still exists");
        }
    }

    private static void VerifyFixtureIfExists(TracerStatus prior)
    {
        if (prior.FixturePath is not null)
        {
            File.Exists(prior.FixturePath).Should().BeTrue("the recorded live frame fixture still exists");
        }
    }

    private static TracerStatus? TryReadStatus()
    {
        try
        {
            if (!File.Exists(StatusPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<TracerStatus>(File.ReadAllText(StatusPath));
        }
        catch
        {
            return null;
        }
    }

    private static void WriteStatus(TracerStatus status)
    {
        status.UpdatedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            File.WriteAllText(StatusPath, JsonSerializer.Serialize(status));
        }
        catch (IOException)
        {
            // relay state is best-effort — the console evidence remains
        }
    }

    private static string PercentDecode(string value) =>
        Uri.UnescapeDataString(value.Replace('+', ' '));

    /// <summary>
    /// gRPC-web frame (1 flag byte 0x00/0x80 + 4-byte big-endian length matching the
    /// remainder) or a plausible protobuf tag (wire type 0-5 in the low 3 bits).
    /// </summary>
    private static bool LooksLikeGrpcWebOrProtobuf(byte[] body)
    {
        if (body.Length == 0)
        {
            return false;
        }

        if (body.Length >= 5 && (body[0] == 0x00 || body[0] == 0x80))
        {
            uint declared = ((uint)body[1] << 24) | ((uint)body[2] << 16) | ((uint)body[3] << 8) | body[4];
            if (declared == body.Length - 5)
            {
                return true;
            }
        }

        return (body[0] & 0x07) < 0x06;
    }

    private static string FindTestProjectDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PlanMeter.Core.Tests.csproj")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Could not locate the test project directory.");
    }

    private sealed class TracerStatus
    {
        [JsonPropertyName("state")] public string State { get; set; } = "";
        [JsonPropertyName("user_code")] public string? UserCode { get; set; }
        [JsonPropertyName("verification_uri")] public string? VerificationUri { get; set; }
        [JsonPropertyName("device_code")] public string? DeviceCode { get; set; }
        [JsonPropertyName("token_endpoint")] public string? TokenEndpoint { get; set; }
        [JsonPropertyName("interval_seconds")] public int IntervalSeconds { get; set; }
        [JsonPropertyName("issued_at_unix_seconds")] public long IssuedAtUnixSeconds { get; set; }
        [JsonPropertyName("expires_at_unix_seconds")] public long ExpiresAtUnixSeconds { get; set; }
        [JsonPropertyName("http_status_code")] public int? HttpStatusCode { get; set; }
        [JsonPropertyName("grpc_status")] public string? GrpcStatus { get; set; }
        [JsonPropertyName("grpc_message")] public string? GrpcMessage { get; set; }
        [JsonPropertyName("body_length")] public int BodyLength { get; set; }
        [JsonPropertyName("hex_prefix")] public string? HexPrefix { get; set; }
        [JsonPropertyName("blob_path")] public string? BlobPath { get; set; }
        [JsonPropertyName("fixture_path")] public string? FixturePath { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("updated_at_unix_seconds")] public long UpdatedAtUnixSeconds { get; set; }
    }
}
