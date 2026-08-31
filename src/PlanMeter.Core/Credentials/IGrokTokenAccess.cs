using System.Threading;
using System.Threading.Tasks;

namespace PlanMeter.Core.Credentials;

/// <summary>
/// Token-lifecycle seam the Grok adapter uses to read a valid access token
/// and to force one refresh on auth failure. Production backing is
/// <see cref="GrokTokenManager"/>.
/// </summary>
internal interface IGrokTokenAccess
{
    Task<string?> GetValidTokenAsync(CancellationToken ct = default);

    Task<string?> ForceRefreshAsync(CancellationToken ct = default);
}
