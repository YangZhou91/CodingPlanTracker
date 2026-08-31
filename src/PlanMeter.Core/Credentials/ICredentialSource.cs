using System.Threading;
using System.Threading.Tasks;

namespace PlanMeter.Core.Credentials;

/// <summary>
/// Per-provider key resolution for the generalized poller (T-02-01). Each provider's
/// poller owns ONE <see cref="ICredentialSource"/>: the Z.ai poller binds the DPAPI
/// key store (<see cref="DpapiKeyStore"/>, which implements this interface); a keyless
/// adapter (Demo) binds <see cref="NullCredentialSource"/>. This is how one poller
/// serves both manual-key and keyless adapters — the poller reads whatever the source
/// returns (possibly null) and always delegates classification to the adapter.
/// </summary>
/// <remarks>
/// SEC-01 read-fresh: a source returns the key freshly read (never held), and the
/// poller never logs it — only the provider id + status (T-02-01 mitigation).
/// </remarks>
public interface ICredentialSource
{
    /// <summary>
    /// Read the provider's key fresh. Returns null when no key is available (blob absent,
    /// corrupt, or a keyless adapter). MUST NOT mutate the credential path.
    /// </summary>
    Task<string?> ReadFreshAsync(CancellationToken ct = default);
}

/// <summary>
/// The no-key credential source — <see cref="ReadFreshAsync"/> always returns null.
/// Used by adapters that need no manual key (e.g. the dev-only Demo adapter), letting
/// the generalized poller call them uniformly.
/// </summary>
public sealed class NullCredentialSource : ICredentialSource
{
    public Task<string?> ReadFreshAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }
}
