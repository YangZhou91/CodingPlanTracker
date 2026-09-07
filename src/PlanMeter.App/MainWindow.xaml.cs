using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PlanMeter.App.Win32;
using PlanMeter.Core.Adapters;
using PlanMeter.Core.Auth;
using PlanMeter.Core.Boot;
using PlanMeter.Core.Config;
using PlanMeter.Core.Credentials;
using PlanMeter.Core.Models;
using PlanMeter.Core.Polling;
using PlanMeter.Core.Refresh;
using PlanMeter.Core.Store;

namespace PlanMeter.App;

/// <summary>
/// PlanMeter compact floating widget. Renders ONE <see cref="ProviderRow"/> per enabled
/// provider (D-23 row stack) in registry order. The strip is a DISPLAY surface only —
/// key management lives in the dedicated <see cref="SettingsWindow"/> (D-01; the Phase-1/2
/// inline key-entry region is retired).
///
/// Per-slot dispatch (D-14 / SC#2 at the UI): a <c>UsageStore.Changed</c> event is routed
/// to the row whose <see cref="ProviderId"/> matches — only that row re-renders.
///
/// D-26 placement: the widget pins its BOTTOM edge at <c>workArea.Bottom - ActualHeight -
/// 16</c> and grows UPWARD as rows stack; <c>SizeChanged</c> re-anchors <c>Top</c> while
/// <c>_userMovedWindow</c> is false (a user-dragged position is never snapped back).
/// </summary>
public partial class MainWindow : Window
{
    private readonly ProviderRegistry _registry;
    private readonly IReadOnlyList<ProviderPoller> _pollers;
    private readonly UsageStore _store;

    /// <summary>D-07 — the per-provider DPAPI store factory. The settings window resolves
    /// each provider card's store via <c>keyStoreFactory(id)</c>. KEY-family NO KEY /
    /// RE-LOGIN classification uses <c>Adapter.Detect()</c> (per-provider blob).</summary>
    private readonly Func<ProviderId, DpapiKeyStore> _keyStoreFactory;

    /// <summary>SC#4 — the 60s global manual-refresh throttle consulted by 'Refresh now' (D-02/D-03).</summary>
    private readonly GlobalRefreshGate _refreshGate;

    /// <summary>D-15 — the read/write ConfigStore ({ pollIntervalSeconds, enabledProviders } at
    /// %LOCALAPPDATA%\PlanMeter\config.json, atomic temp+move write). Passed to the settings
    /// window in Settings_Click so the 03-03 toggle handler persists the enabled-set.</summary>
    private readonly ConfigStore _configStore;

    /// <summary>D-13/D-14 (03-04) — the SHARED, single-global interval source every poller
    /// reads per tick. Handed to the settings window's Polling card so a preset selection
    /// SetInterval's it live (persisting via ConfigStore) — the change applies on the next
    /// scheduled poll without a restart (SC#3). One instance serves ALL providers.</summary>
    private readonly PollIntervalSource _intervalSource;

    /// <summary>GROK-03 — forwarded to the settings Grok login card.</summary>
    private readonly GrokOAuthFlow _grokOAuthFlow;

    /// <summary>GROK-05 — forwarded to the settings Grok login/logout card.</summary>
    private readonly GrokTokenManager _grokTokenManager;

    /// <summary>BOOT-01 — forwarded to the settings Startup card.</summary>
    private readonly BootShortcutManager _bootShortcuts;

    /// <summary>The rows, keyed by provider id — the per-slot dispatch table.</summary>
    private readonly Dictionary<ProviderId, ProviderRow> _rows = new();

    /// <summary>D-01/D-02 — the single owned settings window instance (open-or-activate; a
    /// second Settings… click activates the existing window). Cleared on Closed.</summary>
    private SettingsWindow? _settingsWindow;

    /// <summary>D-26 — set when the user drags the widget; gates the SizeChanged re-anchor.</summary>
    private bool _userMovedWindow;

    public MainWindow(
        ProviderRegistry registry,
        IReadOnlyList<ProviderPoller> pollers,
        UsageStore store,
        Func<ProviderId, DpapiKeyStore> keyStoreFactory,
        GlobalRefreshGate refreshGate,
        ConfigStore configStore,
        PollIntervalSource intervalSource,
        GrokOAuthFlow grokOAuthFlow,
        GrokTokenManager grokTokenManager,
        BootShortcutManager bootShortcuts)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _pollers = pollers ?? throw new ArgumentNullException(nameof(pollers));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _keyStoreFactory = keyStoreFactory ?? throw new ArgumentNullException(nameof(keyStoreFactory));
        _refreshGate = refreshGate ?? throw new ArgumentNullException(nameof(refreshGate));
        _grokOAuthFlow = grokOAuthFlow ?? throw new ArgumentNullException(nameof(grokOAuthFlow));
        _grokTokenManager = grokTokenManager ?? throw new ArgumentNullException(nameof(grokTokenManager));
        _bootShortcuts = bootShortcuts ?? throw new ArgumentNullException(nameof(bootShortcuts));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _intervalSource = intervalSource ?? throw new ArgumentNullException(nameof(intervalSource));

        InitializeComponent();

        BuildRowStack();

        // D-03/D-06 — rebuild the row stack when the enabled-set changes (the settings-window
        // toggle fires EnabledChanged after persisting). Disabling removes the row + parks the
        // poller; re-enabling restores the row in registry order (D-06 immediate fetch lives
        // inside the poller). The event is raised on the UI thread (settings-window handler),
        // so the rebuild runs on the Dispatcher synchronously.
        _registry.EnabledChanged += OnEnabledChanged;

        Loaded += OnLoaded;
        Closed += OnWindowClosed;
    }

    /// <summary>
    /// Build one <see cref="ProviderRow"/> per <c>_registry.Enabled</c> entry in registry
    /// order (PROV-02/ordering — registry order, stable across polls).
    /// Each row: gets a fresh widget-global ContextMenu (right-click on its drag region),
    /// hooks the window-level grab-handle hover cue, and reports drags so the bottom-edge
    /// anchor yields to a user-dragged position. The row's key-entry-requested events are
    /// no longer subscribed here — key management moved to the settings window (D-01;
    /// the ProviderRow events themselves retire in 03-03 Task 2).
    /// </summary>
    private void BuildRowStack()
    {
        var enabled = _registry.Enabled;

        if (enabled.Count == 0)
        {
            // WIDGET-02/empty-stack — every provider disabled or absent (no keyed
            // provider registered): the Phase-3 empty copy + Settings…
            // affordance (W8 re-entry) instead of an empty border. Practically reachable in
            // Debug when the user toggles every provider off.
            EmptyStatePanel.Visibility = Visibility.Visible;
            return;
        }

        // Rebuild path (EnabledChanged): a prior empty state must not linger behind the rows.
        EmptyStatePanel.Visibility = Visibility.Collapsed;

        foreach (var adapter in enabled)
        {
            var row = new ProviderRow(adapter, _store);
            // D-24 — the widget-global ContextMenu (Refresh now / About / Settings… /
            // Quit) is a x:Shared="False" resource; each row's drag region gets its own
            // instance.
            row.DragRegion.ContextMenu = (ContextMenu)FindResource("GlobalMenu");

            // D-09/F5 — the conditional per-row console deep-link item. Because the menu
            // resource is x:Shared="False", each row's instance is its OWN menu, so
            // inserting a per-row item cannot leak across rows. Visible only when the
            // row's adapter declares a console URL (a data check, never a provider-name
            // check). Placement: directly below "Refresh now", above the separator.
            if (row.DragRegion.ContextMenu.Items[0] is MenuItem refreshItem)
            {
                var consoleItem = new MenuItem
                {
                    Header = $"Open {adapter.DisplayName} console",
                    Visibility = adapter.ConsoleUrl is null
                        ? Visibility.Collapsed
                        : Visibility.Visible,
                    // G-05-3 — carry the owning row directly instead of walking the visual
                    // tree in the click handler: DragRegion's parent is the RowOuter Grid,
                    // never the ProviderRow, so a Parent-based lookup always failed.
                    Tag = row,
                };
                consoleItem.Click += OpenConsole_Click;
                row.DragRegion.ContextMenu.Items.Insert(
                    row.DragRegion.ContextMenu.Items.IndexOf(refreshItem) + 1,
                    consoleItem);
            }

            row.DragRegion.MouseEnter += DragRegion_MouseEnter;
            row.DragRegion.MouseLeave += DragRegion_MouseLeave;
            row.UserDragStarted += () => _userMovedWindow = true;
            RowStack.Children.Add(row);
            _rows[adapter.Id] = row;
        }

        // WIDGET-02/rows — 1 DIP Brush.Divider stroke between adjacent rows (N rows →
        // N-1 dividers); the last row carries none.
        for (int i = 0; i < RowStack.Children.Count - 1; i++)
        {
            ((ProviderRow)RowStack.Children[i]).BottomDivider.BorderThickness = new Thickness(0, 0, 0, 1);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // WIDGET-01 — apply WS_EX_TOPMOST | WS_EX_NOACTIVATE so the window never steals
        // keyboard focus on launch / click / refresh.
        WindowExtensions.ApplyTopmostNoActivate(this);

        // WIDGET-05a/D-13 — subscribe to HwndSource.DpiChanged for off-screen clamping.
        // The HwndSource is available after ApplyTopmostNoActivate hooks it.
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            var hwndSource = HwndSource.FromHwnd(hwnd);
            if (hwndSource is not null)
            {
                hwndSource.DpiChanged += OnDpiChanged;
            }
        }
        catch (Exception)
        {
            // HwndSource not yet available — clamp will run on next SizeChanged instead.
        }

        // D-05/D-26 — first-launch default position = bottom-right of the primary work
        // area, inset 16 DIP (Space.Lg). SizeToContent="WidthAndHeight" leaves the sizing
        // dependency properties as NaN at Loaded time; only the measured layout sizes
        // (ActualWidth/ActualHeight) are populated by then. Window.Left/Top carry a
        // ValidateValueCallback that throws ArgumentException on NaN, so the sizing DPs
        // MUST NOT be used here — use the measured post-layout sizes instead.
        Rect workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 16;
        Top = workArea.Bottom - ActualHeight - 16;

        // Subscribe to the store and render the current reading (if any).
        _store.Changed += OnStoreChanged;

        if (_rows.Count == 0)
        {
            // Empty-stack state already shown by BuildRowStack; nothing to render.
            return;
        }

        // Initial render: pull one snapshot and render every row from its own slot; rows
        // without a terminal reading enter their own loading state (static rows and
        // keyless manual-key rows render terminal instead — see RenderRowInitial).
        var snapshot = _store.Snapshot();
        var currentById = new Dictionary<ProviderId, UsageReading?>();
        foreach (var snap in snapshot)
        {
            currentById[snap.Id] = snap.Current;
        }

        foreach (var row in _rows.Values)
        {
            RenderRowInitial(row, currentById.TryGetValue(row.Id, out var current) ? current : null);
        }
    }

    /// <summary>
    /// The manifest-driven initial render for one row (shared by the startup snapshot pass
    /// and the EnabledChanged rebuild): a stored terminal reading renders as-is; a static
    /// row (D-08, no poller) renders its terminal Unsupported state immediately; a
    /// manual-key provider with no stored key renders the NO KEY state; everything else
    /// enters the loading affordance until the first store Changed event.
    /// </summary>
    private void RenderRowInitial(ProviderRow row, UsageReading? current)
    {
        if (current is not null)
        {
            row.Render(current);
            return;
        }

        if (row.Adapter.SupportsUsageApi == false)
        {
            // D-08 — a static row renders TERMINAL, never loading: there is no poller and
            // no fetch coming, so a loading affordance would never resolve.
            // F3/SC#2 (04-02) — DETECTION-AWARE dispatch, keyed ONLY on manifest data:
            //   FloorReason non-null (a floor-row adapter, e.g. OpenCode GO's D-06
            //     verdict) → RenderFloor(Adapter.Detect()) — the badge reflects
            //     DETECTION reality at render time: USAGE N/A when the auth file is
            //     present, NO LOGIN when it is not.
            //   FloorReason null (a plain Unsupported adapter, e.g. the MiniMax
            //     cookie-wall verdict) → the synthesized terminal Unsupported reading
            //     (badge + Adapter.UnsupportedReason tooltip).
            // No provider id, name, or type-check enters this dispatch — a per-provider
            // plan gets its floor row purely from its manifest.
            if (row.Adapter.FloorReason is not null)
            {
                row.RenderFloor(row.Adapter.Detect());
            }
            else
            {
                row.Render(new UsageReading(
                    Provider: row.Adapter.DisplayName,
                    FetchedAtUtc: DateTimeOffset.UtcNow,
                    Status: ReadingStatus.Unsupported,
                    UsedPct: null,
                    RemainingPct: null,
                    MostBindingWindow: default,
                    AllWindows: null,
                    ErrorMessage: null));
            }

            return;
        }

        if (row.Adapter.RequiresManualKey && !_keyStoreFactory(row.Id).BlobPathExists())
        {
            // A manual-key provider with no stored key yet — render the NO KEY state
            // (not loading). Manifest-driven: any future manual-key provider gets the
            // same NO-KEY-at-startup render.
            row.Render(null);
            return;
        }

        // D-01/D-04 (09-02) — ManualOnlyFetch has no startup fetch, so the loading
        // fallthrough would spin forever (or degrade to NO DATA). Manifest-driven
        // (not Codex-hardcoded): Detect true + no stored reading → idle dash +
        // "Refresh to check." tooltip; Detect false → NO LOGIN immediately, never
        // the idle hint (a machine with no login must not advertise Refresh).
        if (row.Adapter.ManualOnlyFetch)
        {
            if (row.Adapter.Detect())
            {
                row.Render(null);
                row.SetIdleHint("Refresh to check.");
                return;
            }

            row.Render(new UsageReading(
                Provider: row.Adapter.DisplayName,
                FetchedAtUtc: DateTimeOffset.UtcNow,
                Status: ReadingStatus.NotLoggedIn,
                UsedPct: null,
                RemainingPct: null,
                MostBindingWindow: default,
                AllWindows: null,
                ErrorMessage: null));
            return;
        }

        // The poller's ExecuteAsync has already kicked off a first fetch on startup.
        // If no terminal reading exists yet, show the loading affordance
        // (WIDGET-03/loading) until the first store Changed event arrives.
        // G-04-4 (04-06 Task 3) — arm the loading watchdog: ~2x the current poll
        // interval. If no store event arrives within that window, the row degrades
        // to an honest "NO DATA" affordance instead of showing the infinite bar.
        row.LoadingTimeout = _intervalSource.Current * 2;
        row.EnterLoadingState();
    }

    private void OnStoreChanged(object? sender, ProviderReadingChangedEventArgs e)
    {
        // D-14 / SC#2 — per-slot dispatch: re-render ONLY the row whose Id matches the
        // event's ProviderId. Slot B's chrome is never touched by slot A's event.
        if (!_rows.TryGetValue(e.Id, out var row))
        {
            return;
        }

        UsageReading? reading = e.Reading;

        // Marshal onto the UI thread. The first terminal reading exits the loading state.
        // WR-09 — guard the BeginInvoke against a shut-down dispatcher. The poller can
        // publish a reading after the window is closing (the _store.Changed subscription
        // at OnLoaded was never unsubscribed, and the host shutdown sequence in App.OnExit
        // stops the poller but window-closing and host-stopping race). Without the guard,
        // a post-shutdown publish would throw InvalidOperationException ("Dispatcher has
        // shut down") on the BeginInvoke — unobservable because the handler runs on a
        // background thread, so it would go to AppDomain.CurrentDomain.UnhandledException
        // and produce a noisy exit. The inline try/catch swallows that specific exception
        // class; legitimate render errors still propagate via the inner Action.
        DispatcherOperation op = Dispatcher.BeginInvoke(new Action(() =>
        {
            row.ExitLoadingState();
            row.Render(reading);
        }));

        // Aborted operations (dispatcher shut down between queueing and execution) surface
        // their failure on the DispatcherOperation.Task; observe it here so an aborted
        // render does not leak as an unobserved-exception noisy exit.
        op.Completed += (_, __) => { /* finished normally */ };
        op.Aborted += (_, __) => { /* dispatcher shut down — drop silently (WR-09) */ };
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Context-menu handlers
    // ─────────────────────────────────────────────────────────────────────────────

    private void RefreshNow_Click(object sender, RoutedEventArgs e)
    {
        // SC#4 / D-02/D-03 — the 60s global manual-refresh throttle. TryAcquire is the
        // single decision point under a lock (T-02-22): a rejected acquire returns BEFORE
        // any poller fan-out, so a second click inside the window is a SILENT NO-OP — no
        // fetch, no visible feedback, no cooldown label, no disabled state (D-03). The menu
        // item stays enabled and identical at rest.
        if (!_refreshGate.TryAcquire())
        {
            return;
        }

        // D-01 — signal EVERY ENABLED provider's poller to wake and fetch. A DISABLED
        // poller is parked (D-06): a refresh signal there is only consumed by the park,
        // and a parked poller never claims the R3 in-flight slot — so RefreshNowAsync
        // would busy-wait its full 15s timeout per click, per disabled poller, for
        // nothing (WR-03). Skip them at the fan-out site. Each enabled poller's R3
        // funneling still collapses a rapid double-click into a single fetch.
        //
        // D-07 (09-02) — after a successful acquire, arm in-flight loading on every
        // enabled ManualOnly row (resolved via _rows[poller.Id]; ProviderPoller does
        // not expose Adapter). A rejected acquire never reaches here (D-06). Auto-poll
        // rows keep EnterLoadingState's Current-guard; only ManualOnly flashes the
        // bar even when a prior reading exists.
        foreach (var poller in _pollers)
        {
            if (!poller.IsEnabled)
            {
                continue;
            }

            if (_rows.TryGetValue(poller.Id, out var row) && row.Adapter.ManualOnlyFetch)
            {
                row.EnterInFlightLoading();
            }

            _ = poller.RefreshNowAsync();
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        // WIDGET-02/About — inline non-modal label (NOT a dialog). Toggle so the
        // menu item stays honest (clicking About again closes the panel).
        AboutPanel.Visibility = AboutPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>
    /// D-09 — the conditional per-row "Open {Provider} console" menu item. The sending
    /// <see cref="MenuItem"/> carries its owning <see cref="ProviderRow"/> in
    /// <see cref="FrameworkElement.Tag"/> (set at insertion time in BuildRowStack, G-05-3 —
    /// a visual-tree walk does NOT work here: DragRegion's direct parent is the RowOuter
    /// Grid, not the row); the row's <see cref="ProviderRow.Adapter"/> supplies the
    /// hard-coded console URL. Shell-launch on explicit user click only; failure is a
    /// silent no-op (the shared catch pair in <see cref="ProviderRow.OpenConsoleUrl"/> —
    /// F5-error, T-04-07).
    /// </summary>
    private void OpenConsole_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ProviderRow { Adapter.ConsoleUrl: string url } })
        {
            return;
        }

        ProviderRow.OpenConsoleUrl(url);
    }

    /// <summary>
    /// D-01/D-02 — open (or activate) the single owned <see cref="SettingsWindow"/>.
    /// A second Settings… click ACTIVATES the existing window instead of opening a second
    /// instance (single-instance at the surface level, mirroring WIDGET-04). The window is
    /// owned by the strip, non-topmost, and user-initiated — <see cref="Window.Activate"/>
    /// is the only activation call here (D-02); the strip itself stays no-activate.
    /// </summary>
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_registry, _keyStoreFactory, _pollers, _store, _configStore, _intervalSource, _grokOAuthFlow, _grokTokenManager, _bootShortcuts)
            {
                // Owned windows render above their owner (the strip); NOT a second topmost
                // HWND. Closing the widget (Quit) closes the owned window.
                Owner = this,
            };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        if (!_settingsWindow.IsVisible)
        {
            _settingsWindow.Show();
        }

        _settingsWindow.Activate();
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Alt+F4 routes to Quit (there is NO close button; UI-SPEC §Window control).
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // The window has NO close (×) button; the only path that reaches OnClosing is
        // Alt+F4 (or Application.Current.Shutdown from Quit). Treat both as Quit:
        // the host stop / shutdown is performed in App.OnExit. No modal save-prompt
        // is permitted (WIDGET-01: no focus steal).
        base.OnClosing(e);
    }

    /// <summary>
    /// WR-09 — unsubscribe from the store AND the registry when the window finishes closing
    /// so a late publish from the poller (which races App.OnExit's host.StopAsync) does not
    /// queue a BeginInvoke onto a dispatcher that is mid-shutdown, and a late EnabledChanged
    /// cannot rebuild rows against a closing strip. Paired with the Aborted handler on the
    /// DispatcherOperation in OnStoreChanged — the unsubscribe is the primary defence, the
    /// Aborted handler is belt-and-braces.
    /// </summary>
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _store.Changed -= OnStoreChanged;
        _registry.EnabledChanged -= OnEnabledChanged;
    }

    /// <summary>
    /// D-03/D-06 — the enabled-set changed (a settings-window toggle persisted + applied).
    /// Rebuild the row stack from <c>Registry.Enabled</c>: clear the strip and re-run
    /// BuildRowStack (which reads the filtered Enabled view in registry order and re-applies
    /// the per-row wiring + dividers exactly as the original build does). Runs on the UI
    /// thread (the toggle handler fires the event on the Dispatcher).
    /// </summary>
    private void OnEnabledChanged(object? sender, ProviderEnabledChangedEventArgs e)
    {
        RowStack.Children.Clear();
        _rows.Clear();
        BuildRowStack();

        // D-08 — a re-enabled STATIC row must re-render TERMINAL, not loading (no poller
        // exists for it, so a loading affordance would never resolve). Re-run the same
        // manifest-driven initial render the startup pass uses for every row.
        var snapshot = _store.Snapshot();
        var currentById = new Dictionary<ProviderId, UsageReading?>();
        foreach (var snap in snapshot)
        {
            currentById[snap.Id] = snap.Current;
        }

        foreach (var row in _rows.Values)
        {
            RenderRowInitial(row, currentById.TryGetValue(row.Id, out var current) ? current : null);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Drag region + grab handle (WIDGET-02/drag; UI-SPEC §Drag region). The row-level
    // DragMove lives in ProviderRow (fires only from DragRegion); this window toggles
    // the grab-handle cue on row DragRegion hover and arms _userMovedWindow on drag.
    // ─────────────────────────────────────────────────────────────────────────────

    private void DragRegion_MouseEnter(object sender, MouseEventArgs e)
    {
        // Opacity is render-only — Visibility Collapsed↔Visible used to resize the
        // SizeToContent HWND by 6 DIP at every row boundary (hover flicker).
        GrabHandle.Opacity = 1;
    }

    private void DragRegion_MouseLeave(object sender, MouseEventArgs e)
    {
        GrabHandle.Opacity = 0;
    }

    /// <summary>
    /// D-26 — bottom-edge anchor. Recompute <c>Top = workArea.Bottom - ActualHeight - 16</c>
    /// on every layout pass that changes the widget height (row stack grows/shrinks), so
    /// the widget grows UPWARD from the bottom-right inset. A user-dragged position is
    /// never snapped back (<c>_userMovedWindow</c> gates the re-anchor for the session).
    /// Uses ActualHeight, never the Height DP (the NaN-at-Loaded caveat). The Left edge
    /// is fixed-width in Phase 2 and needs no recomputation.
    /// </summary>
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_userMovedWindow)
        {
            return;
        }

        Rect workArea = SystemParameters.WorkArea;
        Top = workArea.Bottom - ActualHeight - 16;

        // D-13 — clamp to workspace on size changes (handles DPI re-layout).
        ClampToVisibleWorkspace();
    }

    /// <summary>
    /// D-13 — off-screen clamp-to-workspace. If the widget is outside all monitor
    /// work areas, snaps to the nearest visible area. Called on DpiChanged and
    /// SizeChanged. Only activates when the widget is demonstrably off-screen —
    /// WPF prevents dragging fully off a monitor, so user-dragged positions never
    /// trigger the clamp.
    /// </summary>
    private void ClampToVisibleWorkspace()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) { return; }

            if (FullscreenDetector.IsWindowOffScreen(hwnd))
            {
                var clamped = FullscreenDetector.ClampToNearestWorkspace(
                    Left, Top, ActualWidth, ActualHeight);
                Left = clamped.X;
                Top = clamped.Y;
            }
        }
        catch (Exception)
        {
            // Clamp failure is non-fatal — the widget may be off-screen but at least
            // it doesn't crash. The user can reposition manually.
        }
    }

    /// <summary>
    /// D-13 — HwndSource.DpiChanged handler. When the DPI changes (drag between
    /// monitors, or monitor DPI setting change), clamp the widget to the nearest
    /// visible workspace if it lands off-screen.
    /// </summary>
    private void OnDpiChanged(object sender, HwndDpiChangedEventArgs e)
    {
        // DPI change can leave the widget off-screen on a disconnected/changed monitor.
        ClampToVisibleWorkspace();
    }
}
