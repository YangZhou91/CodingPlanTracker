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
/// Phase 14: every ROW-01..09 state is a 26 DIP single-line content swap. Ok/NearLimit/
/// 0% paint 剩余 xx% via ShowRemaining + QuotaBar; non-figure states paint StatusText
/// via ShowStatus. No under-row StateBadge, no FigureText, no LoadingProgress.
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
    /// when the timer fires (no store event arrived), it degrades to an honest
    /// "—　暂无数据" affordance. Null = no watchdog (design-time / test determinism).
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
    // Paint helpers — Phase 14 single-line content swap
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Collapse bar + remaining host + chip + reset and clear opacities so a previous
    /// figure state cannot leak under a non-figure status line.
    /// </summary>
    private void ResetQuotaArea()
    {
        QuotaBar.Visibility = Visibility.Collapsed;
        QuotaBar.Opacity = 1.0;
        RemainingHost.Visibility = Visibility.Collapsed;
        ChipBorder.Visibility = Visibility.Collapsed;
        ChipBorder.Opacity = 1.0;
        TimestampText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// ROW-04..09 — show a single status line spanning bar+number. Collapses the
    /// remaining host, bar, chip, and reset first.
    /// </summary>
    private void ShowStatus(string copy, Brush brush, object? tooltip = null)
    {
        ResetQuotaArea();
        StatusText.Text = copy;
        StatusText.Foreground = brush;
        StatusText.Visibility = Visibility.Visible;
        StatusText.ToolTip = tooltip;
    }

    /// <summary>
    /// ROW-01..03 / ROW-08 — paint the two-TextBlock remaining host in the 56 DIP
    /// number column. Collapses StatusText. Caller owns QuotaBar fill/opacity and
    /// chip/reset visibility.
    /// </summary>
    private void ShowRemaining(double? remainingPct, Brush valueBrush)
    {
        StatusText.Visibility = Visibility.Collapsed;
        string? value = QuotaRowFormatter.FormatRemainingValue(remainingPct);
        if (value is null)
        {
            // Non-finite / null → fall back to NoData status (DAT-06).
            ShowStatus(RowStateCopy.NoData, (Brush)FindResource("Brush.Dimmed"));
            return;
        }

        RemainingValue.Text = value;
        RemainingValue.Foreground = valueBrush;
        RemainingHost.Visibility = Visibility.Visible;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Render — WIDGET-03 6-state matrix (UI-SPEC §UI Considerations lifted), ported
    // per row. Phase 14: every state is a single-line content swap (ROW-01..09).
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Render a terminal (or null) reading for THIS row only.</summary>
    public void Render(UsageReading? reading)
    {
        if (reading is null)
        {
            // ROW-06 — no data yet. Idle hint moves to StatusText tooltip.
            ShowStatus(RowStateCopy.NoData, (Brush)FindResource("Brush.Dimmed"));
            ClearAllWindowsTooltip();
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
                ShowStatus(RowStateCopy.NoData, (Brush)FindResource("Brush.Dimmed"));
                break;
        }

        // Accessibility — per-row AutomationProperties.Name is namespaced by provider
        // (02-UI-SPEC §Accessibility): "{Provider}, {percent} remaining, {window} window,
        // updated {relative}". A row with a qualifier badge appends the trailing
        // "(estimated)" disambiguator.
        //
        // Per-state override (04-02): the terminal state renders set _stateAutomationName
        // to their fixed per-state names; those names win over the generic sentence here.
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

    /// <summary>ROW-01 — Ok figure: remaining host + Accent bar + chip + reset.</summary>
    private void RenderOk(UsageReading reading)
    {
        if (reading.UsedPct.HasValue)
        {
            // ROW-01/03 — remaining host + remaining-length bar. 0% is data.
            ShowRemaining(reading.RemainingPct, (Brush)FindResource("Brush.OnSurface"));
            QuotaBar.Visibility = Visibility.Visible;
            QuotaBar.Opacity = 1.0;
            ApplyRemainingFill(reading.RemainingPct ?? 0);
            QuotaFill.Background = (Brush)FindResource("Brush.Accent");
            ApplyChip(reading);
            ApplyLiveReset(reading);
        }
        else
        {
            // Empty data:{} → ROW-06 no-data state. Pitfall 5 — never an empty track.
            ShowStatus(
                RowStateCopy.NoData,
                (Brush)FindResource("Brush.Dimmed"),
                HasQualifier
                    ? $"{Adapter.DisplayName} hasn't returned rate-limit headers yet — no estimate available."
                    : $"{Adapter.DisplayName} has not reported usage yet — check back after your first API call.");
            TimestampText.Text = string.Empty;
            TimestampText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>ROW-02 — NearLimit: same hosts, NearLimit brushes on value + fill.</summary>
    private void RenderNearLimit(UsageReading reading)
    {
        ShowRemaining(reading.RemainingPct, (Brush)FindResource("Brush.NearLimit"));
        QuotaBar.Visibility = Visibility.Visible;
        QuotaBar.Opacity = 1.0;
        ApplyRemainingFill(reading.RemainingPct ?? 0);
        QuotaFill.Background = (Brush)FindResource("Brush.NearLimit");
        ApplyChip(reading);
        ApplyLiveReset(reading);
    }

    private void RenderError(UsageReading reading)
    {
        // DATA-03/stale + STALE-vs-ERROR predicate: if a prior successful reading
        // exists AND the current Error reading is older than one interval (10 min),
        // show STALE (last-known remaining % + gray bar + 旧 chip); else hard ERROR.
        var lastSuccessful = _store.LastSuccessful(Id);
        var errorAge = DateTimeOffset.UtcNow - reading.FetchedAtUtc;
        bool isStale = _store.IsStale(Id, reading);

        if (isStale)
        {
            if (lastSuccessful!.HasFigure)
            {
                // ROW-08 — keep last remaining % + gray bar + 旧 chip; no StatusText.
                ShowRemaining(lastSuccessful.RemainingPct, (Brush)FindResource("Brush.Dimmed"));
                QuotaBar.Visibility = Visibility.Visible;
                QuotaBar.Opacity = 0.5;
                ApplyRemainingFill(lastSuccessful.RemainingPct ?? 0);
                QuotaFill.Background = (Brush)FindResource("Brush.Dimmed");
                ApplyLiveReset(lastSuccessful);

                int minutes = (int)Math.Floor(errorAge.TotalMinutes);
                ChipText.Text = QuotaRowFormatter.ChipWithStaleSuffix(lastSuccessful.MostBindingWindow, stale: true);
                ChipBorder.Visibility = Visibility.Visible;
                ChipBorder.Opacity = 0.5;
                ChipBorder.ToolTip = $"旧 — couldn't reach {Adapter.DisplayName} {minutes}m ago. Showing the last reading. We'll retry automatically.";
                ClearAllWindowsTooltip();
            }
            else
            {
                // ROW-08b — stale, no figure → NoData. Never an empty track (DAT-06).
                ShowStatus(RowStateCopy.NoData, (Brush)FindResource("Brush.Dimmed"));
                TimestampText.Text = string.Empty;
                TimestampText.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            // ROW-07 — fresh error: RequestFailed + Error brush.
            string? errorMsg = string.IsNullOrEmpty(reading.ErrorMessage)
                ? $"Couldn't reach {Adapter.DisplayName}. Check your connection; we'll retry in 10 min."
                : reading.ErrorMessage;
            ShowStatus(RowStateCopy.RequestFailed, (Brush)FindResource("Brush.Error"), errorMsg);
            if (reading.AllWindows is { Count: > 0 })
            {
                ApplyAllWindowsTooltip(reading);
            }
            else
            {
                ClearAllWindowsTooltip();
            }
        }
    }

    private void RenderNotLoggedIn(UsageReading reading)
    {
        if (Adapter.AuthFamily == AuthFamily.Session)
        {
            // F4 — the SESSION-family NO LOGIN / RE-LOGIN split.
            if (Adapter.Detect())
            {
                // RE-LOGIN — plain-text re-auth guidance, NO hyperlink.
                string tooltip =
                    Adapter.ReLoginGuidance is string guidance
                        ? $"The {Adapter.DisplayName} session expired. {guidance} — PlanMeter never refreshes tokens itself."
                        : $"The {Adapter.DisplayName} session expired. Re-authorize in the official {Adapter.DisplayName} app — PlanMeter never refreshes tokens itself.";
                ShowStatus(RowStateCopy.NeedLogin, (Brush)FindResource("Brush.Dimmed"), tooltip);
                TimestampText.Visibility = Visibility.Collapsed;
                _stateAutomationName = $"{Adapter.DisplayName}, re-login needed";
            }
            else
            {
                string tooltip =
                    Adapter.ReLoginGuidance is string guidance
                        ? $"{Adapter.DisplayName} isn't logged in on this machine. {guidance} — PlanMeter reads only local state."
                        : $"{Adapter.DisplayName} isn't logged in on this machine. Authorize in the official {Adapter.DisplayName} app — PlanMeter reads only local state.";
                ShowStatus(RowStateCopy.NeedLogin, (Brush)FindResource("Brush.Dimmed"), tooltip);
                TimestampText.Visibility = Visibility.Collapsed;
                _stateAutomationName = $"{Adapter.DisplayName}, not logged in";
            }

            return;
        }

        // KEY family — NO KEY / RE-LOGIN split on THIS adapter's DPAPI blob.
        bool blobExists = Adapter.Detect();
        if (blobExists)
        {
            // 401 with a stored key → RE-LOGIN. Hyperlink tooltip when ConsoleUrl declared.
            ShowStatus(RowStateCopy.NeedLogin, (Brush)FindResource("Brush.Dimmed"), BuildReLoginTooltip());
            _stateAutomationName = $"{Adapter.DisplayName}, re-login needed";
        }
        else
        {
            // ROW-04a — not configured.
            ShowStatus(
                RowStateCopy.NotConfigured,
                (Brush)FindResource("Brush.Dimmed"),
                $"Enter a {Adapter.DisplayName} API key in Settings to see usage. PlanMeter reads it only locally.");
            _stateAutomationName = $"{Adapter.DisplayName}, no key";
        }
    }

    private void RenderUnsupported(UsageReading reading)
    {
        // ROW-09a
        string? tooltip = Adapter.UnsupportedReason
            ?? "PlanMeter doesn't poll this provider — doing so risks your account. Open the provider's own app to see usage.";
        ShowStatus(RowStateCopy.Unsupported, (Brush)FindResource("Brush.Dimmed"), tooltip);
        TimestampText.Visibility = Visibility.Collapsed;
        _stateAutomationName = $"{Adapter.DisplayName}, unsupported";
    }

    /// <summary>
    /// F3/SC#2 — the DETECTION-AWARE floor render (04-02; the D-06 OpenCode shape):
    /// detected → ROW-09b UsageNotAvailable; not detected → ROW-04b NeedLogin.
    /// </summary>
    public void RenderFloor(bool detected)
    {
        if (detected)
        {
            // ROW-09b — UsageNotAvailable
            string tooltip = Adapter.FloorReason
                ?? (Adapter.ReLoginGuidance is string guidance
                    ? $"{Adapter.DisplayName} is logged in, but exposes no usage endpoint PlanMeter can safely read. Check {guidance} for usage."
                    : $"{Adapter.DisplayName} is logged in, but exposes no usage endpoint PlanMeter can safely read.");
            ShowStatus(RowStateCopy.UsageNotAvailable, (Brush)FindResource("Brush.Dimmed"), tooltip);
            _stateAutomationName = $"{Adapter.DisplayName}, logged in, usage not available";
        }
        else
        {
            // ROW-04b — NeedLogin
            string tooltip =
                Adapter.ReLoginGuidance is string guidance
                    ? $"{Adapter.DisplayName} isn't detected on this machine. {guidance} — PlanMeter reads only local state."
                    : $"{Adapter.DisplayName} isn't logged in on this machine. Log in with the official {Adapter.DisplayName} app — PlanMeter reads only local state.";
            ShowStatus(RowStateCopy.NeedLogin, (Brush)FindResource("Brush.Dimmed"), tooltip);
            _stateAutomationName = $"{Adapter.DisplayName}, not logged in";
        }

        TimestampText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// D-01 (09-02) — idle hint for a ManualOnlyFetch row that Detects true and has
    /// no stored reading. Tooltip + automation only. Callers must Render(null) first
    /// so the NoData status is already painted.
    /// </summary>
    public void SetIdleHint(string hint)
    {
        StatusText.ToolTip = hint;
        AutomationProperties.SetName(DragRegion, $"{Adapter.DisplayName}, refresh to check");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Loading state (WIDGET-03/loading). Phase 14: text, not a ProgressBar.
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
    /// Phase 14: if Current(Id) has a figure, skip the loading swap (do not blank a
    /// good row); still arm the watchdog. Timeout is 30s.
    /// </summary>
    public void EnterInFlightLoading()
    {
        LoadingTimeout = TimeSpan.FromSeconds(30);

        // Decision 3 — skip loading swap when a prior figure exists.
        if (_store.Current(Id) is { HasFigure: true })
        {
            ArmLoadingWatchdog();
            return;
        }

        ShowLoadingVisuals();
        ArmLoadingWatchdog();
    }

    private void ShowLoadingVisuals()
    {
        ShowStatus(RowStateCopy.Loading, (Brush)FindResource("Brush.Dimmed"));
    }

    private void ArmLoadingWatchdog()
    {
        // G-04-4 loading watchdog — arm a one-shot timer that degrades to an honest
        // NoData affordance if no store event arrives within the timeout.
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
    }

    /// <summary>
    /// G-04-4 loading watchdog tick — the poller has been silent for ~2 intervals with
    /// no store event. Degrade to ROW-06 NoData. This is an error-SHAPED state, never
    /// a fabricated number (T-04-25).
    /// </summary>
    private void LoadingWatchdog_Tick(object? sender, EventArgs e)
    {
        // Disarm first — one-shot (T-04-26).
        var timer = _loadingWatchdog;
        _loadingWatchdog = null;
        timer?.Stop();

        // If a store event arrived between arming and ticking, ExitLoadingState already
        // handled it. After a prior success EnterInFlightLoading also leaves Current
        // populated — restore that last figure instead of leaving the loading text.
        if (_store.Current(Id) is UsageReading current)
        {
            ExitLoadingState();
            Render(current);
            return;
        }

        // Render the honest ROW-06 NoData degraded affordance.
        ShowStatus(
            RowStateCopy.NoData,
            (Brush)FindResource("Brush.Dimmed"),
            $"{Adapter.DisplayName} hasn't reported yet — still trying.");
        AutomationProperties.SetName(DragRegion,
            $"{Adapter.DisplayName}, no data yet");
    }

    /// <summary>
    /// Apply the most-binding window chip + the DATA-02 all-windows tooltip.
    /// Phase 14: optional stale flag appends " · 旧" (ROW-08).
    /// </summary>
    private void ApplyChip(UsageReading reading, bool stale = false)
    {
        ChipText.Text = QuotaRowFormatter.ChipWithStaleSuffix(reading.MostBindingWindow, stale);
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
    /// G-08-2 — build one all-windows tooltip tree. DAT-07 appends FormatLastUpdateLine
    /// after the window stack, before the QualifierText estimate note.
    /// </summary>
    private StackPanel? BuildAllWindowsTooltip(UsageReading reading)
    {
        if (reading.AllWindows is not { Count: > 0 } all)
        {
            return null;
        }

        var stack = new StackPanel();
        foreach (var w in WindowReadings.DistinctMostBindingByKind(all))
        {
            // DAT-07 — remaining+used + full local timestamp via FormatTooltipLine.
            string line = QuotaRowFormatter.FormatTooltipLine(w, DateTimeOffset.UtcNow);

            stack.Children.Add(new TextBlock
            {
                Text = line,
                Style = (Style)FindResource("TextLabelStyle"),
                Foreground = (Brush)FindResource("Brush.OnSurface.Dimmed"),
            });
        }

        // DAT-07 — provider last-update line.
        stack.Children.Add(new TextBlock
        {
            Text = QuotaRowFormatter.FormatLastUpdateLine(reading.FetchedAtUtc),
            Style = (Style)FindResource("TextLabelStyle"),
            Foreground = (Brush)FindResource("Brush.OnSurface.Dimmed"),
        });

        // D-13 — estimate note is manifest-driven via QualifierText.
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
    /// bar, compact-reset label, AND remaining host. Never share one StackPanel.
    /// </summary>
    private void ApplyAllWindowsTooltip(UsageReading reading)
    {
        ChipBorder.ToolTip = BuildAllWindowsTooltip(reading);
        QuotaBar.ToolTip = BuildAllWindowsTooltip(reading);
        TimestampText.ToolTip = BuildAllWindowsTooltip(reading);
        RemainingHost.ToolTip = BuildAllWindowsTooltip(reading);
    }

    /// <summary>
    /// G-08-2 — drop hover targets so a previous Ok tooltip cannot linger.
    /// </summary>
    private void ClearAllWindowsTooltip()
    {
        ChipBorder.ToolTip = null;
        QuotaBar.ToolTip = null;
        TimestampText.ToolTip = null;
        RemainingHost.ToolTip = null;
        StatusText.ToolTip = null;
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
    /// D-09 — the shared console deep-link hyperlink: plain foreground at rest,
    /// underline ONLY on hover, click opens the URL via
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
    // DragRegion (provider name + bar + number) — the chip, timestamp, and qualifier
    // badge live OUTSIDE DragRegion or outside its hit-target contract, so clicks
    // there never bubble here.
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
