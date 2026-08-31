using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PlanMeter.Core.Adapters;
using PlanMeter.Core.Auth;
using PlanMeter.Core.Boot;
using PlanMeter.Core.Config;
using PlanMeter.Core.Credentials;
using PlanMeter.Core.Models;
using PlanMeter.Core.Polling;
using PlanMeter.Core.Store;

namespace PlanMeter.App;

/// <summary>
/// D-01/D-02 — the dedicated activatable settings window. A normal, non-topmost, owned
/// window (Owner set in code by <see cref="MainWindow.Settings_Click"/>; single-instance
/// open-or-activate). Renders one provider card per registered provider in registry order;
/// for a key-managed provider (<see cref="IProviderAdapter.RequiresManualKey"/>, D-10) the
/// card hosts a key-status row and two swap blocks — exactly one of the three blocks
/// (status / edit / clear-confirm) visible at a time.
///
/// SEC-03/SEC-04: the window performs NO logging and opens no outbound client of its
/// own — the candidate key flows ONLY through the adapter's named allow-listed +
/// redacting client (AllowListHandler + RedactingHandler) via
/// <see cref="IProviderAdapter.TestFetchAsync"/> and then to <see cref="DpapiKeyStore.Protect"/>.
/// The key value is NEVER echoed — only
/// <c>Saved ✓</c> / <c>No key</c> status renders; the <see cref="PasswordBox"/> is masked
/// and blank on open (D-08 rotation = blank re-entry).
/// </summary>
/// <remarks>
/// Plan 03-03 layers the enable/disable toggles on top of this surface: every known
/// provider renders with a toggle (the registry's <c>All</c> — enabled + disabled, D-03),
/// and the toggle handler PERSISTS-THEN-APPLIES (W3): it writes the prospective enabled
/// list to config.json FIRST via the injected <see cref="ConfigStore"/> and only on a
/// successful write mutates the registry (<see cref="ProviderRegistry.SetEnabled"/>) and
/// the poller (<see cref="ProviderPoller.SetEnabled"/>) — on ANY write failure the toggle
/// reverts to the registry's current state (nothing was live-applied) and the window-level
/// error line shows the locked copy. The polling-interval card arrives in 03-04.
/// </remarks>
public partial class SettingsWindow : Window
{
    private readonly ProviderRegistry _registry;
    private readonly Func<ProviderId, DpapiKeyStore> _keyStoreFactory;
    private readonly IReadOnlyList<ProviderPoller> _pollers;
    private readonly UsageStore _store;

    /// <summary>D-15 — the read/write ConfigStore the toggle handler persists
    /// { pollIntervalSeconds, enabledProviders } through (atomic temp+move write).</summary>
    private readonly ConfigStore _configStore;

    /// <summary>D-13/D-14 — the SHARED, single-global interval source (one instance for
    /// EVERY poller, registered AddSingleton in App.xaml.cs). The interval ComboBox reads
    /// and writes it live: the dropdown pre-selects <c>_intervalSource.Current</c>'s nearest
    /// preset; selecting a preset persists via the ConfigStore AND <see cref="PollIntervalSource.SetInterval"/>s
    /// the shared source so the change applies on the NEXT scheduled poll (SC#3, no restart,
    /// no immediate fetch). A toggle (03-03) persisted after an interval change carries the
    /// CURRENT live value — never this source reverting to the stale startup one.</summary>
    private readonly PollIntervalSource _intervalSource;

    /// <summary>GROK-03 — Core device-flow service the Grok login card drives.</summary>
    private readonly GrokOAuthFlow _grokOAuthFlow;

    /// <summary>GROK-05 — PlanMeter-owned token store the Grok login/logout card writes.</summary>
    private readonly GrokTokenManager _grokTokenManager;

    /// <summary>BOOT-01 — Start Menu shell:startup shortcut. File existence is truth.</summary>
    private readonly BootShortcutManager _bootShortcuts;

    /// <summary>W7 — re-entrancy guard for the interval ComboBox's SelectionChanged handler:
    /// the programmatic revert (write-failure restore) must not re-enter the handler and
    /// re-attempt a doomed write.</summary>
    private bool _syncingIntervalSelection;

    public SettingsWindow(
        ProviderRegistry registry,
        Func<ProviderId, DpapiKeyStore> keyStoreFactory,
        IReadOnlyList<ProviderPoller> pollers,
        UsageStore store,
        ConfigStore configStore,
        PollIntervalSource intervalSource,
        GrokOAuthFlow grokOAuthFlow,
        GrokTokenManager grokTokenManager,
        BootShortcutManager bootShortcuts)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _keyStoreFactory = keyStoreFactory ?? throw new ArgumentNullException(nameof(keyStoreFactory));
        _pollers = pollers ?? throw new ArgumentNullException(nameof(pollers));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _intervalSource = intervalSource ?? throw new ArgumentNullException(nameof(intervalSource));
        _grokOAuthFlow = grokOAuthFlow ?? throw new ArgumentNullException(nameof(grokOAuthFlow));
        _grokTokenManager = grokTokenManager ?? throw new ArgumentNullException(nameof(grokTokenManager));
        _bootShortcuts = bootShortcuts ?? throw new ArgumentNullException(nameof(bootShortcuts));

        InitializeComponent();

        // D-03 — one card per provider in REGISTRY order over the FULL registry (All):
        // a disabled provider still renders (with its toggle OFF) so re-enabling is one
        // click here; a fully-disabled strip never strands the user (W8 empty-stack
        // re-entry via the strip's Settings… button).
        foreach (var adapter in _registry.All)
        {
            CardsHost.Children.Add(new ProviderCardView(this, adapter).Root);
        }

        // CONF-03/SC#3 (03-04) — the Polling card: build the preset ComboBox items from
        // PollingDefaults.PresetIntervalSeconds and pre-select the CURRENT live interval's
        // nearest preset. The handler is subscribed AFTER pre-selection so the ctor's
        // SelectedIndex assignment does not fire it (pre-selection is not a user action).
        BuildIntervalCombo();

        // BOOT-01 — derive the checkbox from File.Exists FIRST, then subscribe Click.
        // A programmatic set must not fire the handler (same discipline as BuildIntervalCombo).
        BootToggle.IsChecked = _bootShortcuts.IsEnabled;
        BootToggle.Click += BootToggle_Click;

        Loaded += OnLoaded;
    }

    /// <summary>
    /// Build the interval ComboBox from <see cref="PollingDefaults.PresetIntervalSeconds"/>
    /// ({300, 600, 900, 1800, 3600} → "5 min"…"60 min", D-11 — the dropdown never presents
    /// a sub-5-min value) and pre-select the nearest preset to the LIVE
    /// <see cref="_intervalSource.Current"/>. Pre-selecting from the live source (not the
    /// stale <c>_startupIntervalSeconds</c>) means a window reopened after an interval change
    /// shows the CURRENT interval — never reverting the user's persisted choice on the next
    /// toggle (cross-plan contract, D-13).
    /// </summary>
    private void BuildIntervalCombo()
    {
        foreach (int seconds in PollingDefaults.PresetIntervalSeconds)
        {
            IntervalCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{seconds / 60} min",
                Tag = seconds,
            });
        }

        int liveSeconds = (int)_intervalSource.Current.TotalSeconds;
        int selectedIndex = PollingDefaults.PresetIntervalSeconds
            .Select((seconds, index) => (seconds, index))
            .OrderBy(x => Math.Abs(x.seconds - liveSeconds))
            .First().index;
        IntervalCombo.SelectedIndex = selectedIndex;

        IntervalCombo.SelectionChanged += IntervalCombo_SelectionChanged;
    }

    /// <summary>
    /// CONF-03/SC#3 (D-15/W7) — the interval preset selection handler. PERSIST-THEN-APPLY:
    /// writes <c>{ pollIntervalSeconds, enabledProviders }</c> via the atomic
    /// <see cref="ConfigStore.Write"/> FIRST, and only on success live-applies the new
    /// interval to the shared <see cref="_intervalSource"/> (D-13 — applies on the NEXT
    /// scheduled poll; no immediate fetch). On ANY write failure the ComboBox reverts to the
    /// CURRENT live interval's nearest preset (the source is untouched) and the window-level
    /// error line shows — the UI never appears saved when it isn't (W7/T-03-11).
    /// </summary>
    private void IntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // W7 — a programmatic revert (below) must not re-enter the handler.
        if (_syncingIntervalSelection)
        {
            return;
        }

        if (IntervalCombo.SelectedItem is not ComboBoxItem item || item.Tag is not int seconds)
        {
            return;
        }

        var selected = TimeSpan.FromSeconds(seconds);
        try
        {
            // Persist FIRST (atomic ConfigStore.Write), carrying the CURRENT enabled-set so
            // the interval write never clobbers (or is clobbered by) the enabled state.
            _configStore.Write(
                _configStore.DefaultConfigPath,
                new ConfigData(seconds, _registry.Enabled.Select(p => p.Id).ToArray()));
        }
        catch (Exception)
        {
            // W7 revert — the source is NOT changed; re-select the live interval's nearest
            // preset (the true prior state) and show the locked error line.
            RevertIntervalComboTo(_intervalSource.Current);
            WindowErrorLine.Visibility = Visibility.Visible;
            return;
        }

        WindowErrorLine.Visibility = Visibility.Collapsed;

        // Live-apply ONLY after the write succeeded (D-13 — next scheduled poll).
        _intervalSource.SetInterval(selected);
    }

    /// <summary>
    /// W7 — restore the ComboBox to the nearest preset of <paramref name="interval"/>,
    /// guarded so the programmatic SelectedIndex change does not re-enter the handler.
    /// </summary>
    private void RevertIntervalComboTo(TimeSpan interval)
    {
        int seconds = (int)interval.TotalSeconds;
        int index = PollingDefaults.PresetIntervalSeconds
            .Select((s, i) => (s, i))
            .OrderBy(x => Math.Abs(x.s - seconds))
            .First().i;
        _syncingIntervalSelection = true;
        try
        {
            IntervalCombo.SelectedIndex = index;
        }
        finally
        {
            _syncingIntervalSelection = false;
        }
    }

    /// <summary>
    /// BOOT-01 — Click (NOT Checked/Unchecked) so a programmatic IsChecked revert cannot
    /// re-enter. Persist = create/delete the .lnk on this STA UI thread. On any failure
    /// revert to File.Exists truth and show WindowErrorLine.
    /// </summary>
    private void BootToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box)
        {
            return;
        }

        bool wanted = box.IsChecked == true;
        try
        {
            if (wanted)
            {
                _bootShortcuts.Enable();
            }
            else
            {
                _bootShortcuts.Disable();
            }

            WindowErrorLine.Visibility = Visibility.Collapsed;
        }
        catch (Exception)
        {
            box.IsChecked = _bootShortcuts.IsEnabled;
            WindowErrorLine.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// D-03/D-06/W3 — the per-card enable/disable toggle handler (PERSIST-THEN-APPLY).
    /// Fires on ToggleButton <c>Click</c> (NOT Checked/Unchecked — a programmatic
    /// <c>IsChecked</c> revert must not re-enter the handler).
    /// <para>
    /// Order matters (T-03-06): (1) build the PROSPECTIVE enabled list from the CURRENT
    /// registry state — <c>append id</c> when enabling (a disabled id is absent from
    /// <c>Enabled</c>), <c>exclude id</c> when disabling (an enabled id is present) — so
    /// no duplicate is possible; (2) <c>_configStore.Write(...)</c> FIRST; on ANY failure
    /// revert the toggle to <c>_registry.IsEnabled(id)</c> (the registry was NOT mutated,
    /// so this is the true prior state, not a no-op) and show the window-level locked
    /// error line, returning WITHOUT touching the registry or the poller (the UI never
    /// shows a state the disk doesn't have); (3) only after a successful write:
    /// <c>_registry.SetEnabled(id, isChecked)</c> (fires <c>EnabledChanged</c> → the
    /// strip's row rebuild) then <c>poller.SetEnabled(isChecked)</c> (the re-enable's
    /// immediate first fetch lives inside the poller, D-06).
    /// </para>
    /// </summary>
    private void ToggleProvider_Click(IProviderAdapter adapter, ToggleButton toggle)
    {
        ProviderId id = adapter.Id;
        bool isChecked = toggle.IsChecked == true;

        // (1) prospective enabled list — registration order preserved, no duplicates.
        IReadOnlyList<ProviderId> prospective = isChecked
            ? _registry.Enabled.Select(p => p.Id).Append(id).ToArray()
            : _registry.Enabled.Where(p => p.Id != id).Select(p => p.Id).ToArray();

        // (2) persist FIRST — a failed write mutates neither registry nor poller (W3).
        // CROSS-PLAN CONTRACT (03-03 → 03-04): the interval written alongside the
        // enabled-set is the LIVE shared source (_intervalSource.Current), NOT the stale
        // startup value — a toggle persisted after an interval change must carry the
        // CURRENT interval, or it silently reverts pollIntervalSeconds in config.json
        // (clobbering the interval the user just set). The shared source is the single
        // cadence authority (D-13/D-14).
        try
        {
            _configStore.Write(
                _configStore.DefaultConfigPath,
                new ConfigData((int)_intervalSource.Current.TotalSeconds, prospective));
        }
        catch (Exception)
        {
            // W3 revert — the registry has NOT been mutated, so IsEnabled(id) is the true
            // prior state (the toggle's reset is NOT a no-op). Nothing was live-applied.
            toggle.IsChecked = _registry.IsEnabled(id);
            WindowErrorLine.Visibility = Visibility.Visible;
            return;
        }

        WindowErrorLine.Visibility = Visibility.Collapsed;

        // (3) live-apply only after the write succeeded.
        _registry.SetEnabled(id, isChecked);
        var poller = _pollers.FirstOrDefault(p => p.Id == id);
        if (poller is not null)
        {
            poller.SetEnabled(isChecked);
        }
    }

    /// <summary>
    /// W1/UI-SPEC work-area clamp. Enforced at <c>Loaded</c>: the window's height grows
    /// with content (<c>SizeToContent=Height</c>) to a <see cref="Window.MaxHeight"/> of
    /// <c>workArea.Height - 16</c>; and the <c>CenterOwner</c>-placed rect (the owner is
    /// the bottom-right widget) is shifted so it stays fully inside the work area.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // CR-01 — the former "divergence guard" (re-seeding the shared PollIntervalSource
        // to the startup interval on reopen) is REMOVED. The guard compared the live source
        // against a value captured at window construction — but App DI seeds the source from
        // the SAME PlanMeterConfig.IntervalSeconds as the captured value, so the two are
        // equal at startup. Once the user changed the interval in a prior open of this
        // window (same session), the captured value equals the CHANGED value and differs
        // from the startup constant — so the guard fired on exactly the case it was meant
        // to protect: it silently reverted the user's live cadence to the stale startup
        // value, and the next persist wrote that wrong value to config.json (permanently
        // clobbering the choice). The shared source is the sole cadence authority
        // (D-13/D-14); nothing needs realigning on reopen.

        MaxHeight = SystemParameters.WorkArea.Height - 16;

        Rect wa = SystemParameters.WorkArea;
        if (Top < wa.Top)
        {
            Top = wa.Top;
        }

        if (Left < wa.Left)
        {
            Left = wa.Left;
        }

        double bottom = Top + ActualHeight;
        if (bottom > wa.Bottom)
        {
            Top = Math.Max(wa.Top, wa.Bottom - ActualHeight);
        }

        double right = Left + ActualWidth;
        if (right > wa.Right)
        {
            Left = Math.Max(wa.Left, wa.Right - ActualWidth);
        }
    }

    /// <summary>
    /// One provider card. For a key-managed provider (<see cref="IProviderAdapter.RequiresManualKey"/>,
    /// D-10): a key-status row (<c>Saved ✓</c> / <c>No key</c> + <c>Edit key…</c> /
    /// <c>Clear saved key</c>) and two swap blocks (KeyEditBlock + ClearConfirmBlock) —
    /// exactly one of the three blocks visible at a time (mirrors the retired Phase-1
    /// KeyEditPanel/ClearConfirmPanel swap). Keyless providers render the name + card
    /// shell only.
    ///
    /// The save path is the D-09 test-fetch-on-save flow (generalized from the retired
    /// <c>SaveKeyAsync</c>): <c>TestFetchAsync(candidate)</c> BEFORE <c>Protect</c>;
    /// <c>NotLoggedIn</c>/<c>Error</c>/unexpected show the inline error and do NOT store;
    /// on <c>Ok</c>/<c>NearLimit</c> the key is stored and ONLY the saved provider's poller
    /// refreshes immediately (D-04 gate bypass — Pitfall 4: no global fan-out, no gate).
    /// </summary>
    private sealed class ProviderCardView
    {
        private readonly SettingsWindow _owner;
        private readonly IProviderAdapter _adapter;
        private readonly DpapiKeyStore _keyStore;

        // The three interchangeable blocks (exactly one visible at a time). Assigned in
        // the ctor for key-managed providers only (keyless providers return early).
        private UIElement _statusBlock = null!;
        private UIElement _editBlock = null!;
        private UIElement _clearConfirmBlock = null!;

        // UI elements assigned in the Build* methods below (called from the ctor).
        private TextBlock _statusText = null!;
        private Button _clearButton = null!;
        private PasswordBox _keyInput = null!;
        private TextBlock _inlineError = null!;
        private Button _saveButton = null!;
        private Button _cancelButton = null!;

        // D-01 (10-03) — the three interchangeable Grok login-card blocks.
        private UIElement _oauthNotLoggedInBlock = null!;
        private UIElement _oauthCodePendingBlock = null!;
        private UIElement _oauthLoggedInBlock = null!;
        private TextBlock _oauthIdleStatus = null!;
        private TextBlock _oauthPendingStatus = null!;
        private TextBox _oauthUserCodeBox = null!;
        private TextBlock _oauthVerificationUrl = null!;
        private Button _oauthCopyButton = null!;
        private CancellationTokenSource? _oauthCts;
        private DispatcherTimer? _oauthPollTimer;
        private DeviceFlowStart? _oauthStart;
        private int _oauthPollIntervalSeconds;
        private DateTimeOffset _oauthExpiryDeadline;
        private bool _oauthPollInFlight;

        /// <summary>The card shell (added to the window's <c>CardsHost</c>).</summary>
        public Border Root { get; }

        public ProviderCardView(SettingsWindow owner, IProviderAdapter adapter)
        {
            _owner = owner;
            _adapter = adapter;
            _keyStore = owner._keyStoreFactory(adapter.Id);

            Root = new Border
            {
                Background = (Brush)owner.FindResource("Brush.Surface.Elevated"),
                BorderBrush = (Brush)owner.FindResource("Brush.Divider"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 8),
            };

            var cardContent = new StackPanel { Orientation = Orientation.Vertical };

            // Header row (Grid): provider name (Body 13 Semibold, OnSurface) in column 0;
            // the enable/disable toggle (ProviderToggle style) right-aligned in column 1.
            // UI-SPEC §Provider card header: AutomationProperties.Name = "Enable {Provider}",
            // NO visible label — the provider name is the row label; the switch's on/off is
            // self-evident. IsChecked reflects the CURRENT enabled-set (enabled + disabled
            // both render via registry.All — D-03).
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            header.Children.Add(new TextBlock
            {
                Text = adapter.DisplayName,
                Style = (Style)owner.FindResource("TextBodyStyle"),
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)owner.FindResource("Brush.OnSurface"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            var toggle = new ToggleButton
            {
                Style = (Style)owner.FindResource("ProviderToggle"),
                IsChecked = owner._registry.IsEnabled(adapter.Id),
                VerticalAlignment = VerticalAlignment.Center,
            };
            // Click (not Checked/Unchecked) so the W3 programmatic IsChecked revert never
            // re-enters the handler.
            toggle.Click += (_, _) => owner.ToggleProvider_Click(adapter, toggle);
            AutomationProperties.SetName(toggle, $"Enable {adapter.DisplayName}");
            Grid.SetColumn(toggle, 1);
            header.Children.Add(toggle);

            cardContent.Children.Add(header);

            // D-01/D-03 (10-03) — three-state Grok login card (not-logged-in / code-pending /
            // logged-in) sharing one grid cell, mirroring the key-card swap idiom. Checked
            // BEFORE RequiresManualKey so a flagged provider never falls through to a key field.
            if (adapter.SupportsOAuthLogin)
            {
                var oauthSwap = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                _oauthNotLoggedInBlock = BuildOAuthNotLoggedInBlock();
                _oauthCodePendingBlock = BuildOAuthCodePendingBlock();
                _oauthLoggedInBlock = BuildOAuthLoggedInBlock();
                oauthSwap.Children.Add(_oauthNotLoggedInBlock);
                oauthSwap.Children.Add(_oauthCodePendingBlock);
                oauthSwap.Children.Add(_oauthLoggedInBlock);
                cardContent.Children.Add(oauthSwap);
                Root.Child = cardContent;

                if (owner._grokTokenManager.HasStoredToken)
                {
                    ShowOAuthLoggedIn();
                }
                else
                {
                    ShowOAuthNotLoggedIn();
                }

                owner.Closed += (_, _) => CancelOAuthFlow();
                return;
            }

            if (!adapter.RequiresManualKey)
            {
                // D-10 — keyless providers render name + toggle only.
                Root.Child = cardContent;
                return;
            }

            var swapGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };

            _statusBlock = BuildStatusBlock();
            _editBlock = BuildEditBlock();
            _clearConfirmBlock = BuildClearConfirmBlock();

            // All three blocks share the single grid cell; exactly one is visible at a time.
            swapGrid.Children.Add(_statusBlock);
            swapGrid.Children.Add(_editBlock);
            swapGrid.Children.Add(_clearConfirmBlock);

            cardContent.Children.Add(swapGrid);
            Root.Child = cardContent;

            ShowStatusBlock(saved: _keyStore.BlobPathExists());
        }

        /// <summary>Key-status block (default): status + Edit key… / Clear saved key.</summary>
        private UIElement BuildStatusBlock()
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _statusText = new TextBlock
            {
                Text = _keyStore.BlobPathExists() ? "Saved ✓" : "No key",
                Style = (Style)_owner.FindResource("TextLabelStyle"),
                Foreground = (Brush)_owner.FindResource("Brush.OnSurface.Dimmed"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(_statusText);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var editButton = new Button
            {
                Content = "Edit key…",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0),
            };
            editButton.Click += EditButton_Click;
            actions.Children.Add(editButton);

            _clearButton = new Button
            {
                Content = "Clear saved key",
                Padding = new Thickness(12, 4, 12, 4),
                IsEnabled = _keyStore.BlobPathExists(),
            };
            _clearButton.Click += ClearButton_Click;
            actions.Children.Add(_clearButton);

            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);
            return row;
        }

        /// <summary>
        /// Key-edit block: blank masked PasswordBox (D-08 blank re-entry, never pre-filled),
        /// helper copy, Cancel / Save key, inline error slot. Enter saves, Esc cancels.
        /// </summary>
        private UIElement BuildEditBlock()
        {
            var block = new StackPanel { Orientation = Orientation.Vertical };

            _keyInput = new PasswordBox
            {
                PasswordChar = '•',
                MaxLength = 4096,
                // W5 — scrolls horizontally, never wraps: a WPF PasswordBox is inherently
                // single-line, so overflow text scrolls horizontally by default.
                Background = (Brush)_owner.FindResource("Brush.Surface.Elevated"),
                Margin = new Thickness(0, 0, 0, 4),
            };
            AutomationProperties.SetName(_keyInput, $"{_adapter.DisplayName} API key");
            // W5 — Save is disabled until the field is non-empty.
            _keyInput.PasswordChanged += (_, _) => _saveButton.IsEnabled = _keyInput.Password.Length > 0;
            _keyInput.KeyDown += KeyInput_KeyDown;
            block.Children.Add(_keyInput);

            block.Children.Add(new TextBlock
            {
                Text = "Stored encrypted with Windows DPAPI. PlanMeter never sends this key anywhere except api.z.ai.",
                Style = (Style)_owner.FindResource("TextBodyStyle"),
                Foreground = (Brush)_owner.FindResource("Brush.OnSurface.Dimmed"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            _cancelButton = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0),
            };
            _cancelButton.Click += CancelButton_Click;
            buttons.Children.Add(_cancelButton);

            _saveButton = new Button
            {
                Content = "Save key",
                Padding = new Thickness(12, 4, 12, 4),
                Background = (Brush)_owner.FindResource("Brush.Accent"),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                IsEnabled = false,
            };
            _saveButton.Click += SaveButton_Click;
            buttons.Children.Add(_saveButton);
            block.Children.Add(buttons);

            _inlineError = new TextBlock
            {
                Style = (Style)_owner.FindResource("TextBodyStyle"),
                Foreground = (Brush)_owner.FindResource("Brush.Error"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            block.Children.Add(_inlineError);

            return block;
        }

        /// <summary>Clear-confirm block: inline non-modal prompt (Phase-1 E6 pattern).</summary>
        private UIElement BuildClearConfirmBlock()
        {
            var block = new StackPanel { Orientation = Orientation.Vertical };

            block.Children.Add(new TextBlock
            {
                Text = "Clear saved key?",
                Style = (Style)_owner.FindResource("TextBodyStyle"),
                Foreground = (Brush)_owner.FindResource("Brush.OnSurface"),
                Margin = new Thickness(0, 0, 0, 8),
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var cancel = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0),
            };
            cancel.Click += CancelClearButton_Click;
            buttons.Children.Add(cancel);

            var confirm = new Button
            {
                Content = "Clear",
                Padding = new Thickness(12, 4, 12, 4),
            };
            confirm.Click += ConfirmClearButton_Click;
            buttons.Children.Add(confirm);

            block.Children.Add(buttons);
            return block;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Block swap — exactly one of the three blocks visible at a time.
        // ─────────────────────────────────────────────────────────────────────────

        private void ShowBlock(UIElement visible)
        {
            _statusBlock.Visibility = ReferenceEquals(visible, _statusBlock) ? Visibility.Visible : Visibility.Collapsed;
            _editBlock.Visibility = ReferenceEquals(visible, _editBlock) ? Visibility.Visible : Visibility.Collapsed;
            _clearConfirmBlock.Visibility = ReferenceEquals(visible, _clearConfirmBlock) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowStatusBlock(bool saved)
        {
            // D-08 — status is Saved ✓ / No key ONLY; the value is never echoed.
            _statusText.Text = saved ? "Saved ✓" : "No key";
            _clearButton.IsEnabled = saved;
            ShowBlock(_statusBlock);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Key-status row handlers
        // ─────────────────────────────────────────────────────────────────────────

        private void EditButton_Click(object sender, RoutedEventArgs e)
        {
            // D-08 rotation = blank re-entry: the field is ALWAYS blank on open, never
            // pre-filled (the existing value is never shown).
            _keyInput.Password = string.Empty;
            _inlineError.Visibility = Visibility.Collapsed;
            ShowBlock(_editBlock);
            _keyInput.Focus();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            ShowBlock(_clearConfirmBlock);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Save path (D-09 test-fetch-on-save, D-04 gate bypass, Pitfall 4)
        // ─────────────────────────────────────────────────────────────────────────

        private void KeyInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _ = SaveKeyAsync();
            }
            else if (e.Key == Key.Escape)
            {
                CancelButton_Click(sender, e);
            }
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            await SaveKeyAsync();
        }

        private async Task SaveKeyAsync()
        {
            string candidate = _keyInput.Password;
            _inlineError.Visibility = Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            // W5 — disable Save + all inputs while the test-fetch + Protect run.
            _saveButton.IsEnabled = false;
            _keyInput.IsEnabled = false;
            _cancelButton.IsEnabled = false;

            try
            {
                UsageReading reading;
                try
                {
                    // D-09 — test-fetch BEFORE DpapiKeyStore.Protect. The candidate key
                    // flows ONLY through the adapter's named "zai" client (AllowListHandler
                    // + RedactingHandler — SEC-02/SEC-03). The window never opens a client
                    // of its own.
                    reading = await _adapter.TestFetchAsync(candidate, default);
                }
                catch (Exception)
                {
                    // Assumption A5 — defensive: a throw out of TestFetchAsync degrades to
                    // the reach error; the key is never stored.
                    ShowInlineError($"Couldn't reach {_adapter.DisplayName}. Check your connection and try again.");
                    return;
                }

                switch (reading.Status)
                {
                    case ReadingStatus.NotLoggedIn:
                        // 401 — show inline validation error, do NOT store, keep edit block open.
                        ShowInlineError($"{_adapter.DisplayName} keys look like {KeyShape()} — check the key and try again.");
                        break;

                    case ReadingStatus.Error:
                        ShowInlineError($"Couldn't reach {_adapter.DisplayName}. Check your connection and try again.");
                        break;

                    case ReadingStatus.Ok:
                    case ReadingStatus.NearLimit:
                        try
                        {
                            _keyStore.Protect(candidate);
                        }
                        catch (Exception)
                        {
                            // DPAPI store failure — no key value, no stack (T-03-04).
                            ShowInlineError("Couldn't store the key securely on this machine.");
                            break;
                        }

                        _keyInput.Password = string.Empty;
                        ShowStatusBlock(saved: true);
                        // D-04 bypass — a key save is a CONFIG action, NOT a global refresh:
                        // signal ONLY the saved provider's poller (Pitfall 4 — no fan-out, no gate).
                        // Pitfall 4 (03-03): SKIP a disabled poller — its immediate-fetch path
                        // is parked by design; a refresh signal there would only be consumed
                        // by the park (T-03-09).
                        var poller = _owner._pollers.FirstOrDefault(p => p.Id == _adapter.Id);
                        if (poller is not null && poller.IsEnabled)
                        {
                            _ = poller.RefreshNowAsync();
                        }

                        break;

                    default:
                        ShowInlineError($"{_adapter.DisplayName} returned an unexpected response.");
                        break;
                }
            }
            finally
            {
                _saveButton.IsEnabled = _keyInput.Password.Length > 0;
                _keyInput.IsEnabled = true;
                _cancelButton.IsEnabled = true;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _keyInput.Password = string.Empty;
            _inlineError.Visibility = Visibility.Collapsed;
            ShowStatusBlock(saved: _keyStore.BlobPathExists());
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Clear path (inline non-modal confirm; never names the key value)
        // ─────────────────────────────────────────────────────────────────────────

        private void ConfirmClearButton_Click(object sender, RoutedEventArgs e)
        {
            _keyStore.Clear();
            // _store.Clear fires UsageStore.Changed → the strip's row re-renders its
            // no-key state (D-04/clear).
            _owner._store.Clear(_adapter.Id);
            ShowStatusBlock(saved: false);

            // WR-02 — the cleared row must show the NO KEY badge NOW, not only after the
            // next scheduled poll (up to 10 min away). Clearing the key does NOT itself
            // trigger a fetch, so without this the row would render a bare "—" (Render(null))
            // until the next poll. Mirror the save path (D-04 bypass — signal ONLY this
            // provider's poller, no global fan-out): its empty-key guard short-circuits
            // BEFORE HTTP (ZaiAdapter returns NotLoggedIn for a null key without a network
            // call), so this is cheap and rate-safe. The poll publishes NotLoggedIn →
            // ProviderRow.RenderNotLoggedIn shows the NO KEY badge (the blob no longer
            // exists).
            var poller = _owner._pollers.FirstOrDefault(p => p.Id == _adapter.Id);
            if (poller is not null && poller.IsEnabled)
            {
                _ = poller.RefreshNowAsync();
            }
        }

        private void CancelClearButton_Click(object sender, RoutedEventArgs e)
        {
            ShowStatusBlock(saved: _keyStore.BlobPathExists());
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────────

        private void ShowInlineError(string message)
        {
            // SEC-03 — the inline error never echoes the entered value.
            _inlineError.Text = message;
            _inlineError.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// The key shape hint in the NotLoggedIn copy. Z.ai is the only key-managed
        /// provider in Phase 3, so the Z.ai shape (<c>abc.def...</c>) is hard-coded; a
        /// Phase-4 provider would carry its shape in the manifest.
        /// </summary>
        private static string KeyShape() => "abc.def...";

        // ─────────────────────────────────────────────────────────────────────────
        // Grok OAuth login card (D-01..D-04 / 10-03)
        // ─────────────────────────────────────────────────────────────────────────

        private UIElement BuildOAuthNotLoggedInBlock()
        {
            var block = new StackPanel { Orientation = Orientation.Vertical };
            _oauthIdleStatus = new TextBlock
            {
                Text = "Not logged in",
                Style = (Style)_owner.FindResource("TextLabelStyle"),
                Foreground = (Brush)_owner.FindResource("Brush.OnSurface.Dimmed"),
            };
            block.Children.Add(_oauthIdleStatus);

            var loginButton = new Button
            {
                Content = "Login…",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                IsEnabled = true,
            };
            AutomationProperties.SetName(loginButton, $"Login to {_adapter.DisplayName}");
            loginButton.Click += OAuthLoginButton_Click;
            block.Children.Add(loginButton);
            return block;
        }

        private UIElement BuildOAuthCodePendingBlock()
        {
            var block = new StackPanel { Orientation = Orientation.Vertical, Visibility = Visibility.Collapsed };
            _oauthPendingStatus = new TextBlock
            {
                Text = "Waiting…",
                Style = (Style)_owner.FindResource("TextLabelStyle"),
                Foreground = (Brush)_owner.FindResource("Brush.OnSurface.Dimmed"),
            };
            block.Children.Add(_oauthPendingStatus);

            _oauthUserCodeBox = new TextBox
            {
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontWeight = FontWeights.SemiBold,
                FontSize = 16,
                Margin = new Thickness(0, 4, 0, 0),
            };
            AutomationProperties.SetName(_oauthUserCodeBox, "Grok login code");
            block.Children.Add(_oauthUserCodeBox);

            _oauthVerificationUrl = new TextBlock
            {
                Style = (Style)_owner.FindResource("TextLabelStyle"),
                Foreground = (Brush)_owner.FindResource("Brush.OnSurface.Dimmed"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            };
            block.Children.Add(_oauthVerificationUrl);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 0),
            };
            _oauthCopyButton = new Button
            {
                Content = "Copy",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0),
                IsEnabled = false,
            };
            _oauthCopyButton.Click += OAuthCopyButton_Click;
            actions.Children.Add(_oauthCopyButton);

            var cancelButton = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(12, 4, 12, 4),
            };
            cancelButton.Click += OAuthCancelButton_Click;
            actions.Children.Add(cancelButton);
            block.Children.Add(actions);
            return block;
        }

        private UIElement BuildOAuthLoggedInBlock()
        {
            var row = new Grid { Visibility = Visibility.Collapsed };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = "Logged in ✓",
                Style = (Style)_owner.FindResource("TextLabelStyle"),
                Foreground = (Brush)_owner.FindResource("Brush.OnSurface.Dimmed"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            var logoutButton = new Button
            {
                Content = "Logout",
                Padding = new Thickness(12, 4, 12, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            logoutButton.Click += OAuthLogoutButton_Click;
            Grid.SetColumn(logoutButton, 1);
            row.Children.Add(logoutButton);
            return row;
        }

        private void ShowOAuthNotLoggedIn(string? message = null)
        {
            _oauthIdleStatus.Text = string.IsNullOrEmpty(message) ? "Not logged in" : message;
            _oauthNotLoggedInBlock.Visibility = Visibility.Visible;
            _oauthCodePendingBlock.Visibility = Visibility.Collapsed;
            _oauthLoggedInBlock.Visibility = Visibility.Collapsed;
        }

        private void ShowOAuthCodePending(string status)
        {
            _oauthPendingStatus.Text = status;
            _oauthNotLoggedInBlock.Visibility = Visibility.Collapsed;
            _oauthCodePendingBlock.Visibility = Visibility.Visible;
            _oauthLoggedInBlock.Visibility = Visibility.Collapsed;
        }

        private void ShowOAuthLoggedIn()
        {
            _oauthNotLoggedInBlock.Visibility = Visibility.Collapsed;
            _oauthCodePendingBlock.Visibility = Visibility.Collapsed;
            _oauthLoggedInBlock.Visibility = Visibility.Visible;
        }

        private async void OAuthLoginButton_Click(object sender, RoutedEventArgs e)
        {
            CancelOAuthFlow();
            _oauthUserCodeBox.Text = string.Empty;
            _oauthVerificationUrl.Text = string.Empty;
            _oauthCopyButton.IsEnabled = false;
            ShowOAuthCodePending("Waiting…");

            _oauthCts = new CancellationTokenSource();
            CancellationToken ct = _oauthCts.Token;
            try
            {
                DeviceFlowStart start = await _owner._grokOAuthFlow.StartDeviceFlowAsync(ct)
                    .ConfigureAwait(true);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (!start.Success)
                {
                    ShowOAuthNotLoggedIn(start.FailureMessage);
                    return;
                }

                // Display ONLY the human user_code + verification URL (P2: never device_code).
                _oauthUserCodeBox.Text = start.UserCode;
                _oauthVerificationUrl.Text = start.VerificationUri;
                _oauthCopyButton.IsEnabled = true;
                ShowOAuthCodePending("Enter this code in the browser");
                TryOpenVerificationUri(start.VerificationUri);

                _oauthStart = start;
                _oauthPollIntervalSeconds = Math.Max(1, start.IntervalSeconds);
                _oauthExpiryDeadline = start.IssuedAtUtc.AddSeconds(start.ExpiresIn);
                StartOAuthPollTimer();
            }
            catch (OperationCanceledException)
            {
                // Cancel / window close — card already reverted by CancelOAuthFlow.
            }
        }

        private void OAuthCopyButton_Click(object sender, RoutedEventArgs e)
        {
            string code = _oauthUserCodeBox.Text;
            if (string.IsNullOrEmpty(code))
            {
                return;
            }

            try
            {
                Clipboard.SetText(code);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Clipboard busy — the code stays visible for manual copy.
            }
        }

        private void OAuthCancelButton_Click(object sender, RoutedEventArgs e)
        {
            CancelOAuthFlow();
            ShowOAuthNotLoggedIn();
        }

        private async void OAuthLogoutButton_Click(object sender, RoutedEventArgs e)
        {
            CancelOAuthFlow();
            _owner._grokTokenManager.Logout();

            ProviderPoller? poller = FindGrokPoller();
            if (poller is not null)
            {
                // Publish NotLoggedIn first (null key short-circuits with zero HTTP), then
                // park live. Park AFTER the fetch so PollOnceAsync is not skipped.
                try
                {
                    await poller.RefreshNowAsync().ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown racing logout — still park.
                }

                poller.ParkSession();
            }

            ShowOAuthNotLoggedIn();
        }

        private void StartOAuthPollTimer()
        {
            _oauthPollTimer?.Stop();
            _oauthPollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(_oauthPollIntervalSeconds),
            };
            _oauthPollTimer.Tick += OAuthPollTimer_Tick;
            _oauthPollTimer.Start();
        }

        private async void OAuthPollTimer_Tick(object? sender, EventArgs e)
        {
            if (_oauthPollInFlight || _oauthStart is null)
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= _oauthExpiryDeadline)
            {
                CancelOAuthFlow();
                ShowOAuthNotLoggedIn("Code expired — try again");
                return;
            }

            _oauthPollInFlight = true;
            try
            {
                DeviceFlowStep step = await _owner._grokOAuthFlow
                    .PollTokenAsync(_oauthStart, _oauthCts?.Token ?? CancellationToken.None)
                    .ConfigureAwait(true);

                switch (step)
                {
                    case DeviceFlowStep.Pending:
                        break;
                    case DeviceFlowStep.SlowDown:
                        _oauthPollIntervalSeconds = Math.Min(
                            _oauthPollIntervalSeconds + GrokOAuthFlow.SlowDownIncrementSeconds,
                            GrokOAuthFlow.PollIntervalCapSeconds);
                        if (_oauthPollTimer is not null)
                        {
                            _oauthPollTimer.Interval = TimeSpan.FromSeconds(_oauthPollIntervalSeconds);
                        }

                        break;
                    case DeviceFlowStep.Complete(TokenSet tokens):
                        CancelOAuthFlow();
                        await _owner._grokTokenManager.SaveInitialTokensAsync(tokens).ConfigureAwait(true);
                        FindGrokPoller()?.ClearSessionPark();
                        ShowOAuthLoggedIn();
                        break;
                    case DeviceFlowStep.Expired:
                        CancelOAuthFlow();
                        ShowOAuthNotLoggedIn("Code expired — try again");
                        break;
                    case DeviceFlowStep.Denied:
                        CancelOAuthFlow();
                        ShowOAuthNotLoggedIn("Login was denied.");
                        break;
                    case DeviceFlowStep.Failed(string message):
                        CancelOAuthFlow();
                        ShowOAuthNotLoggedIn(message);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                // Cancel / window close.
            }
            finally
            {
                _oauthPollInFlight = false;
            }
        }

        private void CancelOAuthFlow()
        {
            _oauthPollTimer?.Stop();
            _oauthPollTimer = null;
            try
            {
                _oauthCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // already disposed
            }

            _oauthCts?.Dispose();
            _oauthCts = null;
            _oauthStart = null;
            _oauthPollInFlight = false;
        }

        /// <summary>
        /// T-10-10 — auto-open the verification URL ONLY after host validation.
        /// Plan pin is host == auth.x.ai; live device flow returns accounts.x.ai, so
        /// xAI-owned hosts (HTTPS + x.ai / *.x.ai, 10-01) are also accepted.
        /// </summary>
        private static void TryOpenVerificationUri(string uriText)
        {
            if (!Uri.TryCreate(uriText, UriKind.Absolute, out Uri? uri)
                || uri.Scheme != Uri.UriSchemeHttps)
            {
                return;
            }

            bool isAuthXai = string.Equals(uri.Host, "auth.x.ai", StringComparison.OrdinalIgnoreCase);
            if (!isAuthXai && !GrokOAuthFlow.IsTrustedVerificationUri(uri))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No default browser — the card still displays the URL.
            }
            catch (System.IO.FileNotFoundException)
            {
                // Same — no https handler.
            }
        }

        private ProviderPoller? FindGrokPoller() =>
            _owner._pollers.FirstOrDefault(p => p.Id == _adapter.Id);
    }
}
