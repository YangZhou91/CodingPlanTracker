using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PlanMeter.Core.Credentials;
using PlanMeter.Core.Http;
using PlanMeter.Core.Models;

namespace PlanMeter.Core.Adapters;

/// <summary>
/// CODEX-02 — Codex live session adapter. Issues
/// <c>GET https://chatgpt.com/backend-api/wham/usage</c> through the named "codex"
/// <see cref="HttpClient"/> so the SEC-02 allow-list + SEC-03 redactor handlers run
/// on every call. Timer-polled on the shared ~10-min cycle (REFRESH-04), never writes
/// auth.json, never calls OpenAI platform hosts.
/// </summary>
public sealed class CodexAdapter : IProviderAdapter
{
    /// <summary>
    /// Internal seam for test injection — allows tests to override File.Exists without
    /// touching real filesystem paths. Defaults to the real File.Exists.
    /// </summary>
    internal static Func<string, bool> FileExistsPredicate { get; set; } = System.IO.File.Exists;

    private readonly IHttpClientFactory? _factory;
    private readonly CodexAuthJsonSource _authSource;
    private readonly string _authJsonPath;
    private int _fetchCount;

    /// <summary>
    /// Detect-only default ctor — probes <see cref="CodexAuthJsonSource.DefaultPath"/>.
    /// Fetch paths require the factory ctor.
    /// </summary>
    public CodexAdapter() : this(CodexAuthJsonSource.DefaultPath) { }

    /// <summary>
    /// Production ctor. Optional <paramref name="authSource"/> lets tests inject a temp
    /// path for <c>ReadAccountIdFreshAsync</c>.
    /// </summary>
    public CodexAdapter(IHttpClientFactory factory, CodexAuthJsonSource? authSource = null)
        : this(factory, authSource ?? new CodexAuthJsonSource(), CodexAuthJsonSource.DefaultPath)
    {
    }

    /// <summary>
    /// Internal constructor for Detect test injection — points Detect() at a specific path.
    /// </summary>
    internal CodexAdapter(string authJsonPath)
        : this(factory: null, new CodexAuthJsonSource(authJsonPath), authJsonPath)
    {
    }

    private CodexAdapter(IHttpClientFactory? factory, CodexAuthJsonSource authSource, string authJsonPath)
    {
        _factory = factory;
        _authSource = authSource ?? throw new ArgumentNullException(nameof(authSource));
        _authJsonPath = authJsonPath ?? throw new ArgumentNullException(nameof(authJsonPath));
    }

    /// <summary>The machine identity — "codex".</summary>
    public ProviderId Id => "codex";

    /// <summary>Human-readable name for the row.</summary>
    public string DisplayName => "Codex";

    /// <summary>CODEX-02 — Codex now exposes a programmatic usage endpoint (manual only).</summary>
    public bool SupportsUsageApi => true;

    /// <summary>Session family — no manual key card in settings.</summary>
    public bool RequiresManualKey => false;

    /// <summary>REFRESH-04 — Codex joins the shared ~10-minute timer poll
    /// (user-directed reversal of GRND-03, risk-control risk accepted).
    /// UA stays "codex-cli"; auth.json stays read-only; never self-refreshed.</summary>
    public bool ManualOnlyFetch => false;

    /// <summary>Codex uses the CLI ChatGPT session, not PlanMeter OAuth login.</summary>
    public bool SupportsOAuthLogin => false;

    /// <summary>Codex authenticates with the local CLI session file.</summary>
    public AuthFamily AuthFamily => AuthFamily.Session;

    /// <summary>Console deep-link for the per-row "Open Codex console" menu.</summary>
    public string? ConsoleUrl => "https://chatgpt.com/codex";

    /// <summary>D-12 — never render an ESTIMATED qualifier on Codex.</summary>
    public string? QualifierText => null;

    /// <summary>The row is no longer Unsupported.</summary>
    public string? UnsupportedReason => null;

    /// <summary>Codex is never a floor row.</summary>
    public string? FloorReason => null;

    /// <summary>
    /// D-15 — after a session park, CLI re-login is not enough; PlanMeter must restart.
    /// </summary>
    public string? ReLoginGuidance => "Sign in with the Codex CLI, then restart PlanMeter.";

    /// <summary>
    /// D-13 — Detect is file-path presence only. An environment API key is not a Codex session.
    /// </summary>
    public bool Detect() => FileExistsPredicate(_authJsonPath);

    /// <summary>The number of fetch invocations. Test seam.</summary>
    public int FetchCount => Volatile.Read(ref _fetchCount);

    /// <summary>On-demand test-fetch — same core as the poller path.</summary>
    public async Task<UsageReading> TestFetchAsync(string? apiKey, CancellationToken ct = default)
        => (await FetchCoreAsync(apiKey, ct).ConfigureAwait(false)).Reading;

    /// <summary>The poller's fetch path (timer-polled on the shared ~10-min cycle).</summary>
    public Task<AdapterFetchResult> FetchUsageAsync(string? apiKey, CancellationToken ct = default)
        => FetchCoreAsync(apiKey, ct);

    private async Task<AdapterFetchResult> FetchCoreAsync(string? apiKey, CancellationToken ct)
    {
        Interlocked.Increment(ref _fetchCount);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new AdapterFetchResult(NotLoggedInReading(), null);
        }

        if (_factory is null)
        {
            throw new InvalidOperationException("CodexAdapter requires IHttpClientFactory to fetch usage.");
        }

        HttpClient http = _factory.CreateClient(HttpExtensions.CodexClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, HttpExtensions.CodexUsagePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.TryAddWithoutValidation("User-Agent", "codex-cli");

        string? accountId = await _authSource.ReadAccountIdFreshAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new AdapterFetchResult(ErrorReading("Couldn't reach Codex.", ex), null);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return new AdapterFetchResult(ErrorReading("Couldn't reach Codex.", ex), null);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new AdapterFetchResult(NotLoggedInReading(), null);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } when
                        ? when - DateTimeOffset.UtcNow
                        : null);
                return new AdapterFetchResult(ErrorReading("Codex is unavailable right now.", null), retryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new AdapterFetchResult(ErrorReading("Codex is unavailable right now.", null), null);
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                return new AdapterFetchResult(ErrorReading("Couldn't read Codex's response.", ex), null);
            }

            DateTimeOffset fetchedAt = DateTimeOffset.UtcNow;

            CodexUsageEnvelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<CodexUsageEnvelope>(body) ?? new CodexUsageEnvelope();
            }
            catch (JsonException ex)
            {
                return new AdapterFetchResult(ErrorReading("Codex sent a malformed response.", ex), null);
            }

            return new AdapterFetchResult(CodexNormalizer.Normalize(envelope, fetchedAt), null);
        }
    }

    private static UsageReading NotLoggedInReading() => new(
        Provider: "Codex",
        FetchedAtUtc: DateTimeOffset.UtcNow,
        Status: ReadingStatus.NotLoggedIn,
        UsedPct: null,
        RemainingPct: null,
        MostBindingWindow: default,
        AllWindows: null,
        ErrorMessage: "Codex rejected the session.");

    private static UsageReading ErrorReading(string message, Exception? inner)
    {
        string safe = string.IsNullOrEmpty(inner?.Message)
            ? message
            : $"{message} ({TokenRedactor.Redact(inner.Message)})";
        return new UsageReading(
            Provider: "Codex",
            FetchedAtUtc: DateTimeOffset.UtcNow,
            Status: ReadingStatus.Error,
            UsedPct: null,
            RemainingPct: null,
            MostBindingWindow: default,
            AllWindows: null,
            ErrorMessage: safe);
    }
}
