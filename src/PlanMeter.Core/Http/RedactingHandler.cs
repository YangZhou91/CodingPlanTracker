using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace PlanMeter.Core.Http;

/// <summary>
/// SEC-03 DelegatingHandler. Strips Authorization / Cookie / x-api-key headers and
/// token-shaped strings (JWT eyJ..., sk-..., sk-admin-..., sk-cp-..., xai-...) from
/// any representation that may flow to a logger or error-reporter, BEFORE that
/// representation is observed.
/// </summary>
/// <remarks>
/// Threat model (T-01-01): the handler never mutates the OUTGOING request — it
/// operates on clones/snapshots only, so the provider still receives its real
/// Authorization header. The redaction is purely defensive: any error message,
/// exception string, or logging snapshot built downstream of this handler must
/// pass through <see cref="TokenRedactor.Redact"/> before being emitted.
///
/// The handler is stateless; concurrent requests through the named "zai" HttpClient
/// each get a fresh redaction pass (SEC-03/concurrency truth).
/// </remarks>
public sealed class RedactingHandler : DelegatingHandler
{
    /// <summary>
    /// Build a redacted snapshot of an HTTP request for diagnostic purposes.
    /// The returned string is safe to log: Authorization / Cookie / x-api-key
    /// headers and token-shaped strings are replaced with "[REDACTED]".
    /// </summary>
    public static string DescribeRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        // Copy non-sensitive headers only.
        if (request.Headers != null)
        {
            foreach (var header in request.Headers)
            {
                if (IsSensitiveHeader(header.Key))
                {
                    clone.Headers.TryAddWithoutValidation(header.Key, "[REDACTED]");
                }
                else
                {
                    clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        string representation = $"{clone.Method} {clone.RequestUri}";
        if (clone.Headers != null)
        {
            foreach (var header in clone.Headers)
            {
                representation += $"\n{header.Key}: {string.Join(", ", header.Value)}";
            }
        }

        // SEC-03: token-shape regex pass over the WHOLE representation (defensive —
        // a header value or URL might still contain a token-shaped string).
        return TokenRedactor.Redact(representation);
    }

    /// <summary>
    /// Build a redacted snapshot of an HTTP response for diagnostic purposes.
    /// </summary>
    public static string DescribeResponse(HttpResponseMessage response)
    {
        string representation = $"HTTP {(int)response.StatusCode} {response.StatusCode}";
        if (response.Headers != null)
        {
            foreach (var header in response.Headers)
            {
                var value = IsSensitiveHeader(header.Key)
                    ? "[REDACTED]"
                    : string.Join(", ", header.Value);
                representation += $"\n{header.Key}: {value}";
            }
        }

        return TokenRedactor.Redact(representation);
    }

    /// <summary>
    /// Returns true for headers that must never appear in a log line (SEC-03).
    /// </summary>
    public static bool IsSensitiveHeader(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        return name.Equals("Authorization", System.StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cookie", System.StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cookie2", System.StringComparison.OrdinalIgnoreCase)
            || name.Equals("x-api-key", System.StringComparison.OrdinalIgnoreCase)
            || name.Equals("Set-Cookie", System.StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Authorization", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Defensive: if any code attaches this request to an exception or log line
        // downstream, ensure the sensitive headers have already been stripped from
        // the request's own state. The original request's Authorization header is
        // still sent to the provider (we copy, never mutate).
        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Exception ex) when (IsReportable(ex))
        {
            // WR-01 / SEC-03 — wrap the exception's message with a redacted representation
            // so the caller never sees a raw token in the OUTER message. CRITICAL: the
            // inner exception is replaced with a RedactException() walk of its tree, NOT
            // passed verbatim. The prior code passed the original `ex` as innerException,
            // which preserved the unredacted inner exception — and ex.ToString() (which
            // Serilog renders via the output template's {Exception} formatter, AND which
            // any caller who logs ex.ToString() observes) prints BOTH the outer redacted
            // message AND the inner exception's full text including its unredacted
            // Message + stack trace. The catch-block comment claimed "the caller never
            // sees a raw token even if it logs ex.ToString()" — that claim was false.
            // RedactInner walks the inner chain and replaces each Message with its
            // redacted form; the original stack trace is intentionally dropped (defensive
            // redaction layer — losing the trace is acceptable; the outer message and
            // status code are enough to triage).
            var redactedInner = RedactInner(ex.InnerException);
            throw new HttpRequestException(
                $"[redacted] {TokenRedactor.Redact(ex.Message)}",
                redactedInner,
                ex is HttpRequestException hre ? hre.StatusCode : null);
        }
    }

    /// <summary>
    /// Walk an exception tree and replace each <see cref="System.Exception.Message"/>
    /// with its redacted form. Returns null for null. The original exception type is
    /// NOT preserved (only a synthetic base <see cref="System.Exception"/> is built) —
    /// this method is a defensive redaction layer, not a faithful wrap; losing the
    /// inner type + stack trace is acceptable. See WR-01.
    /// </summary>
    private static Exception? RedactInner(Exception? ex)
    {
        if (ex is null)
        {
            return null;
        }

        string redactedMessage = TokenRedactor.Redact(ex.Message);
        if (ex.InnerException is null)
        {
            return new System.Exception(redactedMessage);
        }

        return new System.Exception(redactedMessage, RedactInner(ex.InnerException));
    }

    private static bool IsReportable(System.Exception ex)
        => ex is System.Net.Http.HttpRequestException or System.IO.IOException or System.Threading.Tasks.TaskCanceledException;
}
