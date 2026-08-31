using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PlanMeter.Core.Adapters;
using PlanMeter.Core.Models;
using PlanMeter.Core.Presentation;
using PlanMeter.Core.Store;

namespace PlanMeter.App;

/// <summary>
/// D-23 — the reusable Phase-1 row chrome as a per-provider user control. One instance
/// per registered provider, stacked in MainWindow's RowStack in registry order
/// (PROV-02/ordering — stable across polls, never re-sorts).
///
/// Row isolation (PROV-02 / D-14 / SC#2 at the UI level): each row renders ONLY its own
/// store slot. MainWindow dispatches a <c>Changed</c> event to the row whose <see cref="Id"/>
/// matches, so a Z.ai update never touches another row's figure and vice versa. The
/// render matrix (RenderOk / RenderNearLimit / RenderError / RenderNotLoggedIn /
/// RenderUnsupported / RenderFloor + the STALE path) evaluates the STALE-vs-ERROR
/// predicate against THIS row's own slot (DATA-03/stale-per-row).
///
/// Phase-4 groundwork (04-02) — the row chrome is fully DATA-DRIVEN, never
/// provider-name-switched: the state-badge text/tooltip is selected by
/// (status × auth-family × verdict) from the adapter manifest, the qualifier badge
/// (the generalized Demo slot) is driven by <see cref="IProviderAdapter.QualifierText"/>,
/// and the console deep-link URL comes from <see cref="IProviderAdapter.ConsoleUrl"/>.
/// </summary>
public partial class ProviderRow : UserControl
{
    /// <summary>The machine identity — the key of this row's store slot (D-13).</summary>
    public ProviderId Id { get; }

    /// <summary>The provider adapter this row renders.</summary>
    public IProviderAdapter Adapter { get; }

    private readonly UsageStore _store;

    /// <summary>Raised when the user starts dragging the widget via this row's drag region.</summary>
    public event Action? UserDragStarted;

    /// <summary>
    /// G-04-4 loading watchdog (04-06 Task 3) — optional timeout for the loading state.
    /// When non-null, <see cref="EnterLoadingState"/> arms a one-shot
    /// <see cref="DispatcherTimer"/> set to this interval; if the row is still in loading
    /// when the timer fires (no store event arrived), it degrades to an honest "NO DATA"
    /// affordance. Null = no watchdog (design-time / test determinism). Set by
    /// MainWindow before entering loading (typically 2x the poll interval).
    /// </summary>
    public TimeSpan? LoadingTimeout { get; set; }

    /// <summary>G-04-4 — the one-shot loading watchdog timer. Armed by
    /// <see cref="EnterLoadingState"/> when <see cref="LoadingTimeout"/> is non-null;
    /// disarmed by <see cref="ExitLoadingState"/>.</summary>
    private DispatcherTimer? _loadingWatchdog;

    /// <summary>
    /// Whether this row's adapter declares a qualifier badge (drives the
    /// "(estimated)" automation-name suffix, UI-SPEC Accessibility).
    /// </summary>
    private bool HasQualifier { get; }

    /// <summary>
    /// 04-02/UI-SPEC Accessibility — when a terminal state render (Unsupported /
    /// NotLoggedIn variants / RenderFloor) sets this, its fixed per-state
    /// DragRegion automation name overrides the generic sentence for THAT render.
    /// Consumed and reset at the bottom of <see cref="Render"/>.
    /// </summary>
    private string? _stateAutomationName;

    public ProviderRow(IProviderAdapter adapter, UsageStore store)
    {
        Adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Id = adapter.Id;

        InitializeComponent();

        ProviderText.Text = adapter.DisplayName;
        AutomationProperties.SetName(ProviderText, adapter.DisplayName);

        // F1/UI-SPEC — the qualifier badge (the generalized persistent badge slot):
        // text + visibility come from the adapter manifest's QualifierText, never a
        // provider-name check. Visible in EVERY state the row renders (a persistent
        // qualifier, unlike the state-conditional badges).
        HasQualifier = !string.IsNullOrEmpty(adapter.QualifierText);
        QualifierBadgeText.Text = adapter.QualifierText ?? string.Empty;
        QualifierBadge.Visibility = HasQualifier ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Render — WIDGET-03 6-state matrix (UI-SPEC §UI Considerations lifted), ported
    // verbatim per row.
    //  - Ok: Accent green figure + chip + relative timestamp; no badge.
    //  - NearLimit: amber figure (advisory — distinct from both Ok green and Error red).
    //  - Error + prior reading: STALE (last-known @ 50% + STALE badge) — evaluated per
    //    row against THIS row's own LastSuccessful (DATA-03/stale-per-row).
    //  - Error + no prior reading: — in red + ERROR badge.
    //  - NotLoggedIn: — in Dimmed + NO KEY / RE-LOGIN badge.
    //  - Unsupported: — in Dimmed + UNSUPPORTED badge (closed under the matrix now).
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Render a terminal (or null) reading for THIS row only.</summary>
    public void Render(UsageReading? reading)
    {
        if (reading is null)
        {
            HideQuotaMeter();
            FigureText.Foreground = (Brush)FindResource("Brush.Dimmed");
            FigureText.Opacity = 1.0;
            QuotaBar.Opacity = 1.0;
            ChipBorder.Visibility = Visibility.Collapsed;
            ChipBorder.Opacity = 1.0;
            ClearAllWindowsTooltip();
            TimestampText.Visibility = Visibility.Collapsed;
            StateBadge.Visibility = Visibility.Collapsed;
            StateBadge.ToolTip = null;
            AutomationProperties.SetName(DragRegion, $"{Adapter.DisplayName}, no usage data yet");
            return;
        }

        // DATA-03 — relative timestamp label (now / 3m / 47m / 2h at 90 min).
        TimestampText.Text = FormatRelative(DateTimeOffset.UtcNow - reading.FetchedAtUtc);
        TimestampText.ToolTip = reading.FetchedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        TimestampText.Visibility = Visibility.Visible;

        switch (reading.Status)
        {
            case ReadingStatus.Ok:
                RenderOk(reading);
                break;

            case ReadingStatus.NearLimit:
                RenderNearLimit(reading);
                break;

            case ReadingStatus.Error:
                RenderError(reading);
                break;

            case ReadingStatus.NotLoggedIn:
                RenderNotLoggedIn(reading);
                break;

            case ReadingStatus.Unsupported:
                RenderUnsupported(reading);
                break;

            default:
                HideQuotaMeter();
                FigureText.Foreground = (Brush)FindResource("Brush.Dimmed");
                ChipBorder.Visibility = Visibility.Collapsed;
                StateBadge.Visibility = Visibility.Collapsed;
                break;
        }

        // Reset opacity for the next render (the STALE path sets it to 0.5).
        if (reading.Status is not ReadingStatus.Error)
        {
            FigureText.Opacity = 1.0;
            ChipBorder.Opacity = 1.0;
        }

        // Accessibility — per-row AutomationProperties.Name is namespaced by provider
        // (02-UI-SPEC §Accessibility): "{Provider}, {percent} remaining, {window} window,
        // updated {relative}". A row with a qualifier badge appends the trailing
        // "(estimated)" disambiguator (UI-SPEC Phase-4 Accessibility — the retired
        // "(demo)" pattern reused for the honesty qualifier).
        //
        // The "{window} window" clause is omitted when UsedPct is null or AllWindows
        // is empty — MostBindingWindow defaults to WindowKind.FiveHour (enum 0), which
        // would otherwise advertise a phantom "5H window" on Q1 / D-08 empty readings
        // (OpenCode has no FiveHour window).
        //
        // Per-state override (04-02, UI-SPEC Phase-4 Accessibility): the terminal
        // state renders set _stateAutomationName to their fixed per-state names
        // ("{Provider}, unsupported" / ", re-login needed" / ", not logged in" /
        // ", no key"); those names win over the generic sentence here. The flag is
        // reset per Render call so a later Ok render falls back to the generic name.
        if (_stateAutomationName is null)
        {
            double? remainingPct = reading.RemainingPct;
            string remainingText = remainingPct.HasValue
                ? $"{remainingPct.Value:F0}% remaining"
                : "no figure";
            string qualifierSuffix = HasQualifier ? " (estimated)" : string.Empty;
            bool includeWindow = reading.UsedPct.HasValue && reading.AllWindows is { Count: > 0 };
            string windowClause = includeWindow
                ? $", {ChipLabel(reading.MostBindingWindow)} window"
                : string.Empty;
            string automationName =
                $"{Adapter.DisplayName}, {remainingText}{windowClause}, updated {FormatRelative(DateTimeOffset.UtcNow - reading.FetchedAtUtc)}{qualifierSuffix}";
            AutomationProperties.SetName(DragRegion, automationName);
        }
        else
        {
            AutomationProperties.SetName(DragRegion, _stateAutomationName);
            _stateAutomationName = null;
        }
    }

    private void RenderOk(UsageReading reading)
    {
        if (reading.UsedPct.HasValue)
        {
            // UIR-01 — remaining-length bar, never a percent string on FigureText.
            FigureText.Text = "—";
            FigureText.Visibility = Visibility.Collapsed;
            QuotaBar.Visibility = Visibility.Visible;
            QuotaBar.Opacity = 1.0;
            ApplyRemainingFill(reading.RemainingPct ?? 0);
            QuotaFill.Background = (Brush)FindResource("Brush.Accent");
            ApplyChip(reading);
            StateBadge.Visibility = Visibility.Collapsed;
            StateBadge.ToolTip = null;
            ApplyLiveReset(reading);
        }
        else
        {
            // Empty data:{} → Q1 non-error no-data state.
            // Pitfall 5 — an empty track must not look exhausted.
            HideQuotaMeter();
            FigureText.Foreground = (Brush)FindResource("Brush.Dimmed");
            ChipBorder.Visibility = Visibility.Collapsed;
            ClearAllWindowsTooltip();
            StateBadge.Visibility = Visibility.Collapsed;
            TimestampText.Text = string.Empty;
            TimestampText.Visibility = Visibility.Collapsed;
            // Surface the no-data tooltip on the figure itself (no chip to host it).
            // F1 seam (04-02): an adapter declaring a qualifier (an ESTIMATED figure)
            // gets the estimate-parameterized copy — the figure is header-derived, and
            // the absence is "no headers yet", not "no usage yet".
            FigureText.ToolTip = HasQualifier
                ? $"{Adapter.DisplayName} hasn't returned rate-limit headers yet — no estimate available."
                : $"{Adapter.DisplayName} has not reported usage yet — check back after your first API call.";
        }
    }

    private void RenderNearLimit(UsageReading reading)
    {
        // UIR-01 / UIR-02 — same remaining meter as Ok, NearLimit brush, no percent string.
        FigureText.Text = "—";
        FigureText.Visibility = Visibility.Collapsed;
        QuotaBar.Visibility = Visibility.Visible;
        QuotaBar.Opacity = 1.0;
        ApplyRemainingFill(reading.RemainingPct ?? 0);
        QuotaFill.Background = (Brush)FindResource("Brush.NearLimit");
        ApplyChip(reading);
        StateBadge.Visibility = Visibility.Collapsed;
        StateBadge.ToolTip = null;
        ApplyLiveReset(reading);
    }

    private void RenderError(UsageReading reading)
    {
        // DATA-03/stale + STALE-vs-ERROR predicate: if a prior successful reading
        // exists AND the current Error reading is older than one interval (10 min),
        // show STALE (last-known @ 50% opacity + STALE badge); else hard ERROR.
        // The predicate is a pure method on UsageStore (UsageStore.IsStale) evaluated
        // against THIS row's own slot (DATA-03/stale-per-row — one row stale does not
        // affect another row's state). The truth lives in Core; this is the thin
        // UI-thread consumer.
        var lastSuccessful = _store.LastSuccessful(Id);
        var errorAge = DateTimeOffset.UtcNow - reading.FetchedAtUtc;

        bool isStale = _store.IsStale(Id, reading);

        if (isStale)
        {
            if (lastSuccessful!.HasFigure)
            {
                FigureText.Text = "—";
                FigureText.Visibility = Visibility.Collapsed;
                QuotaBar.Visibility = Visibility.Visible;
                QuotaBar.Opacity = 0.5;
                ApplyRemainingFill(lastSuccessful.RemainingPct ?? 0);
                QuotaFill.Background = (Brush)FindResource("Brush.Dimmed");
                ApplyLiveReset(lastSuccessful);
            }
            else
            {
                // Pitfall 5 — no-figure lastSuccessful must not paint an empty track.
                HideQuotaMeter();
                FigureText.Foreground = (Brush)FindResource("Brush.Dimmed");
                FigureText.Opacity = 0.5;
                TimestampText.Text = string.Empty;
                TimestampText.Visibility = Visibility.Collapsed;
            }

            ChipText.Text = ChipLabel(lastSuccessful!.MostBindingWindow);
            ChipBorder.Visibility = Visibility.Visible;
            ChipBorder.Opacity = 0.5;
            ClearAllWindowsTooltip();
            StateBadgeText.Text = "STALE";
            StateBadgeText.Foreground = (Brush)FindResource("Brush.OnSurface.Dimmed");
            StateBadge.Visibility = Visibility.Visible;
            int minutes = (int)Math.Floor(errorAge.TotalMinutes);
            StateBadge.ToolTip = $"Couldn't reach {Adapter.DisplayName} {minutes}m ago. Showing the last reading. We'll retry automatically.";
        }
        else
        {
            HideQuotaMeter();
            FigureText.Foreground = (Brush)FindResource("Brush.Error");
            FigureText.Opacity = 1.0;
            ChipBorder.Visibility = Visibility.Collapsed;
            ChipBorder.Opacity = 1.0;
            if (reading.AllWindows is { Count: > 0 })
            {
                ApplyAllWindowsTooltip(reading);
            }
            else
            {
                ClearAllWindowsTooltip();
            }
            StateBadgeText.Text = "ERROR";
            StateBadgeText.Foreground = (Brush)FindResource("Brush.Error");
            StateBadge.Visibility = Visibility.Visible;
            StateBadge.ToolTip = string.IsNullOrEmpty(reading.ErrorMessage)
                ? $"Couldn't reach {Adapter.DisplayName}. Check your connection; we'll retry in 10 min."
                : reading.ErrorMessage;
        }
    }

    private void RenderNotLoggedIn(UsageReading reading)
    {
        HideQuotaMeter();
        FigureText.Foreground = (Brush)FindResource("Brush.Dimmed");
        ChipBorder.Visibility = Visibility.Collapsed;
        ClearAllWindowsTooltip();

        if (Adapter.AuthFamily == AuthFamily.Session)
        {
            // F4 — the SESSION-family NO LOGIN / RE-LOGIN split, on the NAMED render
            // discriminant (04-02 Task 3): the poller parks a session ONLY on
            // (NotLoggedIn AND Session-family AND credential-present) — i.e. a
            // credential existed but the provider rejected it. ProviderRow cannot see
            // ProviderPoller.SessionParked directly, so the classification is carried
            // by Detect() re-evaluated AT RENDER TIME on the parked reading's terminal
            // shape: a parked session's credential file still exists (Detect() true =>
            // RE-LOGIN); a never-detected session has no credential at all (Detect()
            // false => NO LOGIN — the poller never parks). The two never collide: a
            // never-detected row cannot satisfy Detect() == true.
            if (Adapter.Detect())
            {
                // RE-LOGIN — plain-text re-auth guidance, NO hyperlink (re-auth happens
                // in the official app, not a webpage; UI-SPEC F4/D-05).
                StateBadgeText.Text = "RE-LOGIN";
                StateBadgeText.Foreground = (Brush)FindResource("Brush.OnSurface.Dimmed");
                StateBadge.Visibility = Visibility.Visible;
                StateBadge.ToolTip =
                    Adapter.ReLoginGuidance is string guidance
                        ? $"The {Adapter.DisplayName} session expired. {guidance} — PlanMeter never refreshes tokens itself."
                        : $"The {Adapter.DisplayName} session expired. Re-authorize in the official {Adapter.DisplayName} app — PlanMeter never refreshes tokens itself.";
                // D-05 — a stopped row never renders a timestamp (a frozen relative
                // time would lie; no further polls occur).
                TimestampText.Visibility = Visibility.Collapsed;
                _stateAutomationName = $"{Adapter.DisplayName}, re-login needed";
            }
            else
            {
                StateBadgeText.Text = "NO LOGIN";
                StateBadgeText.Foreground = (Brush)FindResource("Brush.OnSurface.Dimmed");
                StateBadge.Visibility = Visibility.Visible;
                StateBadge.ToolTip =
                    Adapter.ReLoginGuidance is string guidance
                        ? $"{Adapter.DisplayName} isn't logged in on this machine. {guidance} — PlanMeter reads only local state."
                        : $"{Adapter.DisplayName} isn't logged in on this machine. Authorize in the official {Adapter.DisplayName} app — PlanMeter reads only local state.";
                TimestampText.Visibility = Visibility.Collapsed;
                _stateAutomationName = $"{Adapter.DisplayName}, not logged in";
            }

            return;
        }

        // KEY family — NO KEY / RE-LOGIN split on THIS adapter's DPAPI blob
        // (Adapter.Detect(), never the injected Z.ai store). Poll-backed: polls
        // continue, so the timestamp keeps rendering.
        bool blobExists = Adapter.Detect();
        if (blobExists)
        {
            // 401 with a stored key → RE-LOGIN. The console hyperlink renders only when
            // the adapter declares a URL (D-09); a null-URL provider gets plain text.
            StateBadgeText.Text = "RE-LOGIN";
            StateBadgeText.Foreground = (Brush)FindResource("Brush.OnSurface.Dimmed");
            StateBadge.Visibility = Visibility.Visible;
            StateBadge.ToolTip = BuildReLoginTooltip();
            _stateAutomationName = $"{Adapter.DisplayName}, re-login needed";
        }
        else
        {
            StateBadgeText.Text = "NO KEY";
            StateBadgeText.Foreground = (Brush)FindResource("Brush.OnSurface.Dimmed");
            StateBadge.Visibility = Visibility.Visible;
            // 03-UI-SPEC amended copy — key entry lives only in the settings window, so the
            // tooltip says where: "in Settings to see usage".
            StateBadge.ToolTip = $"Enter a {Adapter.DisplayName} API key in Settings to see usage. PlanMeter reads it only locally.";
            _stateAutomationName = $"{Adapter.DisplayName}, no key";
        }
    }

    private void RenderUnsupported(UsageReading reading)
    {
        HideQuotaMeter();
        FigureText.Foreground = (Brush)FindResource("Brush.Dimmed");
        ChipBorder.Visibility = Visibility.Collapsed;
        ClearAllWindowsTooltip();
        StateBadgeText.Text = "UNSUPPORTED";
        StateBadgeText.Foreground = (Brush)FindResource("Brush.OnSurface.Dimmed");
        StateBadge.Visibility = Visibility.Visible;
        // D-10/E3 — the verdict tooltip is per-provider adapter data when declared; the
        // former hard-coded string is the generic default, not the only value.
        StateBadge.ToolTip = Adapter.UnsupportedReason
            ?? "PlanMeter doesn't poll this provider — doing so risks your account. Open the provider's own app to see usage.";
        // UI-SPEC timestamp rule — a poller-less row never renders a timestamp.
        TimestampText.Visibility = Visibility.Collapsed;
        _stateAutomationName = $"{Adapter.DisplayName}, unsupported";
    }

    /// <summary>
    /// F3/SC#2 — the DETECTION-AWARE floor render (04-02; the D-06 OpenCode shape):
    /// detected (the auth file is present at render time) renders the USAGE N/A badge
    /// with the <see cref="IProviderAdapter.FloorReason"/> tooltip — logged in, but no
    /// safe usage endpoint; not detected renders the NO LOGIN badge with the
    /// session-family NO-LOGIN tooltip. Both collapse the timestamp (no fetch ever
    /// happened). Called by MainWindow's SHARED static-row path keyed on
    /// FloorReason — no per-provider branch ever reaches here.
    /// </summary>
    public void RenderFloor(bool detected)
    {
        HideQuotaMeter();
        FigureText.Foreground = (Brush)FindResource("Brush.Dimmed");
        FigureText.Opacity = 1.0;
        ChipBorder.Visibility = Visibility.Collapsed;
        ChipBorder.Opacity = 1.0;
        ClearAllWindowsTooltip();
        TimestampText.Visibility = Visibility.Collapsed;

        if (detected)
        {
            StateBadgeText.Text = "USAGE N/A";
            StateBadgeText.Foreground = (Brush)FindResource("Brush.OnSurface.Dimmed");
            StateBadge.Visibility = Visibility.Visible;
            StateBadge.ToolTip = Adapter.FloorReason
                ?? (Adapter.ReLoginGuidance is string guidance
                    ? $"{Adapter.DisplayName} is logged in, but exposes no usage endpoint PlanMeter can safely read. Check {guidance} for usage."
                    : $"{Adapter.DisplayName} is logged in, but exposes no usage endpoint PlanMeter can safely read.");
            _stateAutomationName = $"{Adapter.DisplayName}, logged in, usage not available";
        }
        else
        {
            StateBadgeText.Text = "NO LOGIN";
            StateBadgeText.Foreground = (Brush)FindResource("Brush.OnSurface.Dimmed");
            StateBadge.Visibility = Visibility.Visible;
            StateBadge.ToolTip =
                Adapter.ReLoginGuidance is string guidance
                    ? $"{Adapter.DisplayName} isn't detected on this machine. {guidance} — PlanMeter reads only local state."
                    : $"{Adapter.DisplayName} isn't logged in on this machine. Log in with the official {Adapter.DisplayName} app — PlanMeter reads only local state.";
            _stateAutomationName = $"{Adapter.DisplayName}, not logged in";
        }
    }

    /// <summary>
    /// D-01 (09-02) — idle hint for a ManualOnlyFetch row that Detects true and has
    /// no stored reading. Tooltip + automation only; never a StateBadge (the copy
    /// will not fit the 180 DIP row). Callers must Render(null) first so the dash /
    /// no-badge / no-timestamp chrome is already painted.
    /// </summary>
    public void SetIdleHint(string hint)
    {
        FigureText.ToolTip = hint;
        AutomationProperties.SetName(DragRegion, $"{Adapter.DisplayName}, refresh to check");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Loading state (WIDGET-03/loading). ProgressBar replaces the figure while a
    // fetch is in-flight and no terminal reading exists. Clears on the first
    // terminal reading (MainWindow's per-slot dispatch calls ExitLoadingState first).
    // ─────────────────────────────────────────────────────────────────────────────

    public void EnterLoadingState()
    {
        // Don't enter loading if a terminal reading is already rendered.
        if (_store.Current(Id) is not null)
        {
            return;
        }

        ShowLoadingVisuals();
        ArmLoadingWatchdog();
    }

    /// <summary>
    /// D-07 (09-02) — in-flight loading for a ManualOnlyFetch row on global Refresh.
    /// Copies <see cref="EnterLoadingState"/> visuals but does NOT return when a
    /// prior reading exists (that guard is why a second Refresh after success
    /// would keep the last figure frozen). Timeout is 30s = 2× the named-client
    /// 15s HttpClient timeout, not 2× the poll interval.
    /// </summary>
    public void EnterInFlightLoading()
    {
        LoadingTimeout = TimeSpan.FromSeconds(30);
        ShowLoadingVisuals();
        ArmLoadingWatchdog();
    }

    private void ShowLoadingVisuals()
    {
        if (SystemParameters.ClientAreaAnimation)
        {
            LoadingProgress.Visibility = Visibility.Visible;
            LoadingGlyph.Visibility = Visibility.Collapsed;
        }
        else
        {
            // E10 reduced-motion: static … glyph instead of the animated ProgressBar.
            LoadingProgress.Visibility = Visibility.Collapsed;
            LoadingGlyph.Visibility = Visibility.Visible;
        }

        FigureText.Visibility = Visibility.Collapsed;
        QuotaBar.Visibility = Visibility.Collapsed;
        ChipBorder.Visibility = Visibility.Collapsed;
        TimestampText.Visibility = Visibility.Collapsed;
        StateBadge.Visibility = Visibility.Collapsed;
    }

    private void ArmLoadingWatchdog()
    {
        // G-04-4 loading watchdog — arm a one-shot timer that degrades to an honest
        // NO DATA affordance if no store event arrives within the timeout. The timer
        // is UI-thread-only (DispatcherTimer), one-shot (no re-arm loop), and disarmed
        // by ExitLoadingState (T-04-26).
        if (LoadingTimeout is TimeSpan timeout)
        {
            _loadingWatchdog?.Stop();
            _loadingWatchdog = new DispatcherTimer { Interval = timeout, IsEnabled = false };
            _loadingWatchdog.Tick += LoadingWatchdog_Tick;
            _loadingWatchdog.IsEnabled = true;
        }
    }

    public void ExitLoadingState()
    {
        // G-04-4 — disarm the watchdog when the store event arrives (normal exit).
        _loadingWatchdog?.Stop();
        _loadingWatchdog = null;

        LoadingProgress.Visibility = Visibility.Collapsed;
        LoadingGlyph.Visibility = Visibility.Collapsed;
        QuotaBar.Visibility = Visibility.Collapsed;
        // Do not force FigureText Visible — the following Render owns figure-vs-bar.
    }

    /// <summary>
    /// G-04-4 loading watchdog tick — the poller has been silent for ~2 intervals with
    /// no store event. Degrade to an honest "NO DATA" affordance: figure "—", dimmed,
    /// badge "NO DATA", tooltip "{Provider} hasn't reported yet — still trying."
    /// This is an error-SHAPED state, never a fabricated number (T-04-25).
    /// </summary>
    private void LoadingWatchdog_Tick(object? sender, EventArgs e)
    {
        // Disarm first — one-shot (T-04-26).
        var timer = _loadingWatchdog;
        _loadingWatchdog = null;
        timer?.Stop();

        // If a store event arrived between arming and ticking, ExitLoadingState already
        // handled it. After a prior success EnterInFlightLoading also leaves Current
        // populated — restore that last figure instead of leaving the spinner (D-02)
        // or fabricating a percent (T-09-09).
        if (_store.Current(Id) is UsageReading current)
        {
            ExitLoadingState();
            Render(current);
            return;
        }

        // Exit the visual loading state (collapse the indeterminate bar).
        LoadingProgress.Visibility = Visibility.Collapsed;
        LoadingGlyph.Visibility = Visibility.Collapsed;
        HideQuotaMeter();

        // Render the honest NO DATA degraded affordance.
        FigureText.Foreground = (Brush)FindResource("Brush.Dimmed");
        FigureText.Opacity = 1.0;
        ChipBorder.Visibility = Visibility.Collapsed;
        ChipBorder.Opacity = 1.0;
        ClearAllWindowsTooltip();
        TimestampText.Visibility = Visibility.Collapsed;
        StateBadgeText.Text = "NO DATA";
        StateBadgeText.Foreground = (Brush)FindResource("Brush.OnSurface.Dimmed");
        StateBadge.Visibility = Visibility.Visible;
        StateBadge.ToolTip =
            $"{Adapter.DisplayName} hasn't reported yet — still trying.";
        AutomationProperties.SetName(DragRegion,
            $"{Adapter.DisplayName}, no data yet");
    }

    /// <summary>
    /// Apply the most-binding window chip + the DATA-02 all-windows tooltip (a
    /// StackPanel listing every parsed window line-per-window). Chip vocabulary
    /// is the fixed token set {5H, WEEK, MONTH, ROLL} — uppercase Label per UI-SPEC.
    /// Each line appends "· resets in {relative}" when the window carries a
    /// ResetsAtUtc (D-03/D-04), omitted silently when null (D-05).
    /// G-08-2 — tooltip assignment is delegated so the used-% figure and the chip
    /// each get an independently-built tree (a FrameworkElement cannot parent twice).
    /// </summary>
    private void ApplyChip(UsageReading reading)
    {
        ChipText.Text = ChipLabel(reading.MostBindingWindow);
        ChipBorder.Visibility = Visibility.Visible;

        if (reading.AllWindows is { Count: > 0 })
        {
            ApplyAllWindowsTooltip(reading);
        }
        else
        {
            ClearAllWindowsTooltip();
        }
    }

    /// <summary>
    /// G-08-2 — build one all-windows tooltip tree. Callers that assign the same
    /// content to two ToolTip properties must call this twice; a single
    /// FrameworkElement cannot be parented to two ToolTip hosts.
    /// </summary>
    private StackPanel? BuildAllWindowsTooltip(UsageReading reading)
    {
        if (reading.AllWindows is not { Count: > 0 } all)
        {
            return null;
        }

        var stack = new StackPanel();
        foreach (var w in all)
        {
            // UIR-04 — used% + full local timestamp via FormatTooltipLine; remaining implied.
            // QualifierText-gated estimate line below is left in place (no-op after UIR-03).
            string line = QuotaRowFormatter.FormatTooltipLine(w, DateTimeOffset.UtcNow);

            stack.Children.Add(new TextBlock
            {
                Text = line,
                Style = (Style)FindResource("TextLabelStyle"),
                Foreground = (Brush)FindResource("Brush.OnSurface.Dimmed"),
            });
        }

        // D-13 — estimate note is manifest-driven via QualifierText, not a Grok special-case.
        if (!string.IsNullOrEmpty(Adapter.QualifierText))
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Estimated from billing data",
                Style = (Style)FindResource("TextLabelStyle"),
                Foreground = (Brush)FindResource("Brush.OnSurface.Dimmed"),
            });
        }

        return stack;
    }

    /// <summary>
    /// G-08-2 — assign independently-built all-windows trees to the chip, remaining
    /// bar, AND compact-reset label so hovering any of the three surfaces shows the
    /// same ROLL/WEEK/MONTH lines (UIR-04). Never share one StackPanel.
    /// </summary>
    private void ApplyAllWindowsTooltip(UsageReading reading)
    {
        ChipBorder.ToolTip = BuildAllWindowsTooltip(reading);
        QuotaBar.ToolTip = BuildAllWindowsTooltip(reading);
        TimestampText.ToolTip = BuildAllWindowsTooltip(reading);
    }

    /// <summary>
    /// G-08-2 — drop hover targets so a previous Ok tooltip cannot linger on
    /// an em dash, NO DATA figure, unrefreshed STALE chip, or leftover reset label.
    /// </summary>
    private void ClearAllWindowsTooltip()
    {
        ChipBorder.ToolTip = null;
        QuotaBar.ToolTip = null;
        TimestampText.ToolTip = null;
        FigureText.ToolTip = null;
    }

    /// <summary>
    /// UIR-01 — write remaining-length star columns from RemainingFill. Never recompute
    /// remaining from UsedPct in the view.
    /// </summary>
    private void ApplyRemainingFill(double remainingPct)
    {
        var (fillStars, emptyStars) = QuotaRowFormatter.RemainingFill(remainingPct);
        QuotaFillCol.Width = new GridLength(fillStars, GridUnitType.Star);
        QuotaEmptyCol.Width = new GridLength(emptyStars, GridUnitType.Star);
    }

    /// <summary>
    /// Collapse the remaining meter and restore the em-dash figure so a previous Ok
    /// fill cannot leak under a non-figure state (Pitfall 4 / Pitfall 5).
    /// </summary>
    private void HideQuotaMeter()
    {
        QuotaBar.Visibility = Visibility.Collapsed;
        FigureText.Visibility = Visibility.Visible;
        FigureText.Text = "—";
    }

    /// <summary>
    /// UIR-02 — compact until-reset from the same window as the bar. Null/elapsed
    /// collapses the slot with no placeholder. Figure-bearing live paints only.
    /// </summary>
    private void ApplyLiveReset(UsageReading source)
    {
        string? compact = QuotaRowFormatter.FormatCompactUntil(
            QuotaRowFormatter.LookupMostBindingReset(source),
            DateTimeOffset.UtcNow);
        if (compact is not null)
        {
            TimestampText.Text = compact;
            TimestampText.Visibility = Visibility.Visible;
        }
        else
        {
            TimestampText.Text = string.Empty;
            TimestampText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// The KEY-family RE-LOGIN tooltip (D-04/401 truth). When the adapter declares a
    /// console URL (D-09) the tooltip carries the underlined-on-hover hyperlink
    /// (UI-SPEC §Copywriting error-state-auth-failed-401); a null-URL provider gets
    /// plain-text guidance. The hyperlink is a UI element the USER clicks; PlanMeter
    /// never calls Process.Start on it automatically (T-01-11). The URL is a
    /// hard-coded adapter constant, never a provider response (T-04-07).
    /// </summary>
    private object BuildReLoginTooltip()
    {
        var container = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
        };
        container.Style = (Style)Application.Current.FindResource("TextLabelStyle");
        container.Foreground = (Brush)Application.Current.FindResource("Brush.OnSurface.Dimmed");

        container.Inlines.Add(new Run($"{Adapter.DisplayName} rejected the key. "));
        if (Adapter.ConsoleUrl is string url)
        {
            container.Inlines.Add(BuildConsoleHyperlink(url, $"Open the {Adapter.DisplayName} console"));
            container.Inlines.Add(new Run(", or rotate the key in Settings."));
        }
        else
        {
            container.Inlines.Add(new Run("Rotate the key in Settings."));
        }

        return container;
    }

    /// <summary>
    /// D-09 — the shared console deep-link hyperlink (extracted from the former
    /// Z.ai-hardcoded RE-LOGIN tooltip): plain foreground at rest, underline ONLY on
    /// hover (<see cref="TextDecorations"/> null ↔ Underline), click opens the URL via
    /// <c>Process.Start(UseShellExecute=true)</c>. A click that cannot open a browser
    /// is a silent no-op (the Win32Exception / FileNotFoundException catch pair —
    /// F5-error, T-04-07).
    /// </summary>
    private static Hyperlink BuildConsoleHyperlink(string url, string label)
    {
        var link = new Hyperlink(new Run(label))
        {
            NavigateUri = new Uri(url),
            TextDecorations = null, // underline ONLY on hover (MouseEnter below)
        };
        link.RequestNavigate += (_, __) => OpenConsoleUrl(url);
        link.MouseEnter += (_, __) => link.TextDecorations = TextDecorations.Underline;
        link.MouseLeave += (_, __) => link.TextDecorations = null;
        return link;
    }

    /// <summary>
    /// D-09/F5-error — shell-launch a console URL on an explicit user click only.
    /// Failure (no default browser / no https handler) is a silent no-op.
    /// </summary>
    public static void OpenConsoleUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No default browser / shell association — the click is a no-op.
        }
        catch (System.IO.FileNotFoundException)
        {
            // Same — no handler registered for the https scheme.
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Drag region (WIDGET-02/drag; UI-SPEC §Drag region). DragMove fires ONLY from
    // DragRegion (provider name + figure area) — the chip, timestamp, and qualifier badge
    // live OUTSIDE DragRegion or outside its hit-target contract, so clicks there
    // never bubble here. The _userMovedWindow flag (D-26) is communicated to MainWindow
    // via <see cref="UserDragStarted"/>.
    // ─────────────────────────────────────────────────────────────────────────────

    private void DragRegion_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Window.GetWindow(this)?.DragMove();
        }
        catch (InvalidOperationException)
        {
            /* DragMove throws if called when not mouse-driven */
        }

        UserDragStarted?.Invoke();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers — chip label + relative timestamp (UI-SPEC §Copywriting).
    // ─────────────────────────────────────────────────────────────────────────────

    private static string ChipLabel(WindowKind kind) => QuotaRowFormatter.ChipLabel(kind);

    private static string FormatRelative(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
        {
            return "now";
        }

        if (age < TimeSpan.FromMinutes(60))
        {
            return $"{(int)age.TotalMinutes}m";
        }

        // Crosses to "2h" at 90 min per UI-SPEC §Copywriting.
        if (age < TimeSpan.FromMinutes(90))
        {
            return "1h";
        }

        return $"{(int)Math.Floor(age.TotalHours)}h";
    }
}
