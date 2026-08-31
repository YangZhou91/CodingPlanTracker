using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace PlanMeter.Core.Http;

/// <summary>
/// SEC-02 DelegatingHandler. Refuses any outbound request whose Host is not on the
/// hard-coded provider allow-list. Refusal happens BEFORE <see cref="DelegatingHandler.SendAsync"/>
/// runs, so the request never leaves the machine. Threat model T-01-03.
/// </summary>
/// <remarks>
/// Match rule: <c>string.Equals(request.RequestUri.Host, allowed, OrdinalIgnoreCase)</c>.
/// NO suffix, wildcard, or subdomain matching — <c>evilapi.z.ai</c> and
/// <c>api.z.ai.evil.com</c> are both refused. The exception message names ONLY the
/// refused host (never the full URL with path/query, never headers).
///
/// The handler is stateless; concurrent requests through the named "zai" HttpClient
/// each get the allow-list check applied per-request (SEC-02/concurrency truth).
/// </remarks>
public sealed class AllowListHandler : DelegatingHandler
{
    private readonly AllowListOptions _options;
    private readonly ILogger<AllowListHandler>? _logger;

    public AllowListHandler(AllowListOptions options, ILogger<AllowListHandler>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is null)
        {
            throw new InvalidOperationException(
                "AllowListHandler: request has no RequestUri — refused (SEC-02).");
        }

        string host = request.RequestUri.Host;
        if (string.IsNullOrEmpty(host))
        {
            throw new InvalidOperationException(
                "AllowListHandler: request host is empty — refused (SEC-02).");
        }

        // Exact-match against the allow-list set. AllowListOptions.AllowedHosts is a
        // case-insensitive set so the comparison is effectively OrdinalIgnoreCase.
        if (!_options.AllowedHosts.Contains(host))
        {
            throw new InvalidOperationException(
                $"AllowListHandler: host '{host}' is not on the provider allow-list — refused (SEC-02).");
        }

        // D-11 — log each allowed egress request. SEC-03: host name ONLY — never
        // the full URL, never headers, never body.
        _logger?.LogInformation("AllowListHandler: egress allowed (host={Host})", host);

        return base.SendAsync(request, cancellationToken);
    }
}
