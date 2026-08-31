using System;
using System.Collections.Generic;

namespace PlanMeter.Core.Http;

/// <summary>
/// SEC-02 — options carrying the hard-coded allow-list of provider hosts.
/// Phase 1 allow-list: { "api.z.ai" }. The set is exact-match only (no
/// wildcard / suffix / subdomain matching) — see AllowListHandler.
/// </summary>
public sealed class AllowListOptions
{
    /// <summary>
    /// The hosts the app is allowed to talk to. Match is EXACT, case-insensitive.
    /// </summary>
    public ISet<string> AllowedHosts { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
