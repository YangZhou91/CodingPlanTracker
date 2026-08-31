using System;
using FluentAssertions;
using PlanMeter.Core.Refresh;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// REFRESH-02 / SC#4 — boundary + silent-no-op + first-click-allowed tests for the 60s
/// global manual-refresh throttle (<see cref="GlobalRefreshGate"/>), all clock-injected via
/// the <c>nowUtc</c> seam (no sleeps, fixed base time).
/// </summary>
/// <remarks>
/// Pins the D-02/D-03 semantics:
/// <list type="bullet">
///   <item>a first acquire is always allowed (null <c>_lastAcquiredUtc</c>),</item>
///   <item>a second acquire inside the window is REJECTED — the silent no-op that returns
///   before any poller fan-out (D-03, T-02-22),</item>
///   <item>the boundary is INCLUSIVE — exactly one <see cref="GlobalRefreshGate.Window"/>
///   after the last acquire opens a new window (<c>now - last &gt;= Window</c> grants).</item>
/// </list>
/// </remarks>
public sealed class GlobalRefreshGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryAcquire_with_no_prior_call_returns_true()
    {
        // A first 'Refresh now' click is always allowed (null _lastAcquiredUtc).
        var gate = new GlobalRefreshGate();

        gate.TryAcquire(T0).Should().BeTrue();
    }

    [Fact]
    public void TryAcquire_second_call_inside_window_is_rejected()
    {
        // A second click inside the 60s window is a SILENT NO-OP (D-03) — rejected before
        // any fetch fan-out.
        var gate = new GlobalRefreshGate();

        gate.TryAcquire(T0).Should().BeTrue();
        gate.TryAcquire(T0 + TimeSpan.FromSeconds(59)).Should().BeFalse();
    }

    [Fact]
    public void TryAcquire_at_exactly_one_window_elapsed_grants()
    {
        // Boundary INCLUSIVE: now - last >= Window opens a new window — the 60th-second
        // boundary grants the next acquire.
        var gate = new GlobalRefreshGate();

        gate.TryAcquire(T0).Should().BeTrue();
        gate.TryAcquire(T0 + TimeSpan.FromSeconds(60)).Should().BeTrue();
    }

    [Fact]
    public void TryAcquire_after_window_elapsed_grants_again()
    {
        // A click after 60s elapses triggers fetches again (the window re-opens).
        var gate = new GlobalRefreshGate();

        gate.TryAcquire(T0).Should().BeTrue();
        gate.TryAcquire(T0 + TimeSpan.FromSeconds(61)).Should().BeTrue();
    }

    [Fact]
    public void TryAcquire_uses_utcnow_when_nowUtc_not_supplied()
    {
        // The production path (no seam) is a live UtcNow read — the first call on a fresh
        // gate must still acquire. Determinism for the boundary is covered by the
        // clock-injected cases above.
        var gate = new GlobalRefreshGate();

        gate.TryAcquire().Should().BeTrue();
    }
}
