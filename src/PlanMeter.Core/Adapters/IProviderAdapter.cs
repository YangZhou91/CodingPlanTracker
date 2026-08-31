using System;
using System.Threading;
using System.Threading.Tasks;
using PlanMeter.Core.Models;

namespace PlanMeter.Core.Adapters;

/// <summary>
/// The load-bearing provider-adapter port (D-10/D-11). The scheduler, the keyed store,
/// and the UI depend ONLY on this interface — never on a provider's concrete type —
/// so adding a provider is one entry in the flat provider list (SC#1) and one provider's
/// behavior can never couple into another's poller/store/row (SC#2).
///
/// The port's METHOD SHAPE is locked now (costly to change once ≥2 adapters implement it);
/// the static descriptor is intentionally minimal — <see cref="Id"/>, <see cref="DisplayName"/>,
/// <see cref="SupportsUsageApi"/> — and grows later (auth_type, rate_tolerance) in
/// Phase 3/4 when a real adapter needs those fields (D-10 YAGNI).
/// </summary>
public interface IProviderAdapter
{
    /// <summary>The machine identity — the key of the store / registry / poller slot.</summary>
    ProviderId Id { get; }

    /// <summary>Human-readable name for the row and the <c>UsageReading.Provider</c> field.</summary>
    string DisplayName { get; }

    /// <summary>
    /// PROV-04 — whether this provider exposes programmatic usage access at all. A
    /// provider with <c>SupportsUsageApi == false</c> renders an honest Unsupported row
    /// ("—"), never a fabricated figure.
    /// </summary>
    bool SupportsUsageApi { get; }

    /// <summary>
    /// D-10 — whether this provider needs a manually-entered API key stored via the
    /// per-provider DPAPI store (<c>DpapiKeyStore.ForProvider(Id)</c>). The settings
    /// window renders a key field per this signal (Plan 03-02); keyless providers
    /// (e.g. the Demo mock) return false and render no key row. Phase-4 adapters plug
    /// in without a redesign — this is the manifest seam the window branches on.
    /// </summary>
    bool RequiresManualKey { get; }

    /// <summary>
    /// GRND-02 — when true, the poller NEVER creates a PeriodicTimer and NEVER
    /// performs a startup fetch. Fetches occur ONLY on an explicit RefreshNowAsync
    /// signal.
    /// </summary>
    bool ManualOnlyFetch { get; }

    /// <summary>
    /// GRND-02/D-07 — when true, the settings window renders a login card instead of
    /// a key-entry field. A RENDER flag only — orthogonal to AuthFamily and
    /// RequiresManualKey.
    /// </summary>
    bool SupportsOAuthLogin { get; }

    /// <summary>
    /// Phase-4 groundwork (04-02) — the provider's authentication family. Drives the
    /// row's NotLoggedIn render split (UI-SPEC Phase-4 Row-State Vocabulary): the KEY
    /// family keeps the NO KEY / RE-LOGIN-on-blob-existence split; the SESSION family
    /// splits NO LOGIN (never detected) vs RE-LOGIN (parked session, D-05). Adapter-
    /// declared data — the badge never guesses.
    /// </summary>
    AuthFamily AuthFamily { get; }

    /// <summary>
    /// D-09 — the provider's console/account deep-link URL, or null when the provider
    /// declares none. Drives the tooltip hyperlink and the conditional
    /// <c>Open {Provider} console</c> per-row menu item. Hard-coded adapter constant
    /// only — NEVER sourced from a provider response (T-04-07).
    /// </summary>
    string? ConsoleUrl { get; }

    /// <summary>
    /// F1/UI-SPEC — the persistent qualifier badge text (e.g. "ESTIMATED" for Grok's
    /// header-derived figure), or null for no qualifier badge. Null also means the
    /// DragRegion automation name carries no "(estimated)" suffix.
    /// </summary>
    string? QualifierText { get; }

    /// <summary>
    /// D-10 verdict tooltip for an Unsupported row — the per-provider honest reason a
    /// user can distinguish deliberate-unsupported from broken. Null when the adapter
    /// supports usage (the row then never renders Unsupported). The UI falls back to
    /// its generic string when null-unsupported, keeping the old copy as the
    /// per-provider default.
    /// </summary>
    string? UnsupportedReason { get; }

    /// <summary>
    /// D-06 floor tooltip — non-null ONLY on a floor-row adapter (logged in, usage
    /// N/A): the shared static-row render path keys on this to call RenderFloor() and
    /// select the badge by <see cref="Detect()"/> at render time. Null = a plain
    /// Unsupported (or polling) adapter.
    /// </summary>
    string? FloorReason { get; }

    /// <summary>
    /// D-16 — provider-specific re-login guidance text for session-family providers.
    /// When non-null, replaces the generic "Re-authorize in the official {Provider} app"
    /// phrasing in tooltip strings (NO LOGIN and RE-LOGIN badges). Null = keep existing
    /// parameterized text (KEY-family and Unsupported providers).
    /// </summary>
    string? ReLoginGuidance { get; }

    /// <summary>
    /// PROV-03 — local presence probe: is this provider's credential present on this
    /// machine? READ-ONLY (File.Exists / FileShare.Read), no network, no side effects,
    /// never writes or mutates a credential path (SEC-01). Phase-2 scope: DPAPI-blob
    /// existence only; real per-provider credential-path probing lands with each
    /// Phase-4/5 adapter (D-12).
    /// </summary>
    bool Detect();

    /// <summary>
    /// D-04 — test-fetch BEFORE persisting a candidate key (the Save-key handler calls
    /// this with a freshly-typed key; on NotLoggedIn the caller MUST NOT store it).
    /// </summary>
    Task<UsageReading> TestFetchAsync(string? apiKey, CancellationToken ct = default);

    /// <summary>
    /// The poller's fetch path. Classifies every outcome into a <see cref="ReadingStatus"/>
    /// reading — never throws for "weird data" (Pattern A). The result also carries
    /// <see cref="AdapterFetchResult.RetryAfter"/> so the poller can implement the
    /// REFRESH-03 cross-tick 429 backoff WITHOUT touching the <see cref="UsageReading"/>
    /// shape (RESEARCH §Normalizer verbatim — do NOT add fields to UsageReading).
    /// </summary>
    Task<AdapterFetchResult> FetchUsageAsync(string? apiKey, CancellationToken ct = default);
}

/// <summary>
/// The provider's authentication family (Phase-4 groundwork, 04-02). KEY = a manual
/// API key stored in PlanMeter's DPAPI store (Z.ai, MiniMax-if-shipped); SESSION = a
/// locally-logged-in OAuth session PlanMeter reuses read-only (Grok, OpenCode).
/// Drives the row's NotLoggedIn render vocabulary (NO KEY vs NO LOGIN / RE-LOGIN)
/// and the poller's session-park eligibility (D-05).
/// </summary>
public enum AuthFamily
{
    Key,
    Session,
}

/// <summary>
/// The fetch outcome: the classified <see cref="UsageReading"/> plus the optional
/// <c>Retry-After</c> duration from a 429 response. The RetryAfter carrier keeps the
/// backoff signal off the <see cref="UsageReading"/> record (which is pinned verbatim
/// by RESEARCH.md §Normalizer Design).
/// </summary>
/// <param name="Reading">The classified reading (Ok / NearLimit / Error / NotLoggedIn / Unsupported).</param>
/// <param name="RetryAfter">The <c>Retry-After</c> duration when the upstream returned 429; null otherwise.</param>
public sealed record AdapterFetchResult(UsageReading Reading, TimeSpan? RetryAfter);
