using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlanMeter.Core.Credentials;

/// <summary>
/// GROK-05 / Pitfall 3 — the poller's "grok" credential source over the DPAPI-backed
/// <see cref="GrokTokenManager"/>. PASSIVE by contract: <see cref="ReadFreshAsync"/>
/// returns the STORED access token (possibly stale) exactly when a readable blob
/// exists, else null. Lifecycle refresh is NOT this source's job — the adapter's
/// 401-retry path drives <see cref="GrokTokenManager.ForceRefreshAsync"/> (10-02).
/// </summary>
/// <remarks>
/// The null/non-null split IS the poller's session-park discriminator
/// (ProviderPoller.PollOnceAsync: NotLoggedIn + Session family + key non-null):
/// null → NO LOGIN (pre-login guidance); non-null + NotLoggedIn reading → park →
/// "Please re-login". No HTTP client is ever created here — the source is a pure
/// read-fresh view over the blob (SEC-01).
/// </remarks>
public sealed class GrokCredentialSource : ICredentialSource
{
    private readonly GrokTokenManager _manager;

    public GrokCredentialSource(GrokTokenManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    /// <summary>Null iff no readable blob exists; otherwise the stored access token (may be stale).</summary>
    public Task<string?> ReadFreshAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_manager.PeekStoredAccessToken());
    }
}
