using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PlanMeter.Core.Adapters;
using PlanMeter.Core.Auth;
using PlanMeter.Core.Boot;
using PlanMeter.Core.Config;
using PlanMeter.Core.Credentials;
using PlanMeter.Core.Http;
using PlanMeter.Core.Logging;
using PlanMeter.Core.Models;
using PlanMeter.Core.Polling;
using PlanMeter.Core.Refresh;
using PlanMeter.Core.Store;
using PlanMeter.App.Win32;
using Serilog;
using Serilog.Events;

namespace PlanMeter.App;

/// <summary>
/// PlanMeter application bootstrap.
/// </summary>
// SEC-04: no telemetry/crash-upload/auto-updater — do not add. The only outbound
// traffic PlanMeter ever originates is the allow-listed named clients 'zai',
// 'minimax', and 'opencode' (see Core/Http/AllowListHandler.cs). Adding any SDK
// that performs out-of-process data flow (ApplicationInsights, Sentry,
// crash-upload, auto-updater) violates a foundational invariant of this product
// — see REQUIREMENTS.md SEC-04.
public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    /// <summary>
    /// WIDGET-04 — the named <see cref="EventWaitHandle"/> a SECOND process signals to
    /// bring this instance's window forward. Local\ scope only; a signal only re-asserts
    /// topmost, never activates (T-02-06 mitigation).
    /// </summary>
    private EventWaitHandle? _bringToFrontHandle;

    /// <summary>Stops the bring-forward watcher on exit (cancel BEFORE disposing the handle).</summary>
    private CancellationTokenSource? _bringToFrontCts;

    /// <summary>Stops the fullscreen watcher on exit (linked to _bringToFrontCts).</summary>
    private CancellationTokenSource? _fullscreenCts;

    private IHost? _host;

    /// <summary>
    /// The deferring named-client factory handed to the eagerly-constructed adapter list
    /// inside ConfigureServices (no service provider exists there). Bound to the real
    /// factory between host Build and Start — before any adapter can fetch.
    /// </summary>
    private DeferredHttpClientFactory? _deferredHttpClientFactory;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WIDGET-04 (Phase 2 requirement borrowed forward per UI-SPEC): named-Mutex
        // single-instance guard so dogfooding never shows two overlapping widgets.
        //
        // The mutex is checked FIRST — before any log-file I/O — because a second process
        // must not collide on the shared Serilog log file (the first instance holds it
        // exclusively). A second launch signals the first instance's bring-forward handle
        // and exits WITHOUT opening the log file.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: @"Local\PlanMeter_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // WIDGET-04/SC#5 — a second launch must NOT duplicate the window: signal the
            // existing instance's bring-forward handle (Local\PlanMeter_BringToFront) and
            // exit. The first instance's watcher re-asserts topmost WITHOUT stealing focus.
            using (var bringForward = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                @"Local\PlanMeter_BringToFront",
                out bool handleCreated))
            {
                if (!handleCreated)
                {
                    bringForward.Set();
                    Log.Information("PlanMeter: another instance is already running. Signalled it forward; shutting down.");
                }
                else
                {
                    // The first instance hasn't created the handle yet (startup race) — the
                    // signal would be lost. The first instance creates it shortly and starts
                    // waiting; nothing to forward. Exit quietly.
                    Log.Information("PlanMeter: another instance is already running (bring-forward handle not yet created). Shutting down.");
                }
            }

            Current.Shutdown();
            return;
        }

        GC.KeepAlive(_singleInstanceMutex);

        // WIDGET-04 — the first instance owns the bring-forward handle and starts a watcher
        // (after the window is shown) that re-asserts topmost on a second-instance signal.
        _bringToFrontHandle = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            @"Local\PlanMeter_BringToFront",
            out _);
        _bringToFrontCts = new CancellationTokenSource();
        _fullscreenCts = CancellationTokenSource.CreateLinkedTokenSource(_bringToFrontCts.Token);

        // SEC-04: global exception handlers use ONLY the existing local Serilog file sink
        // (wired below). No telemetry / crash-upload / auto-updater SDK is added. The goal
        // is OBSERVABLE crashes, not hidden ones — both handlers deliberately let the
        // exception propagate so the process still terminates normally.

        // UI-Dispatcher thread: an unhandled exception here would silently kill the WinExe
        // process (no console). Log it, then leave e.Handled = false so the default crash
        // still occurs — we want to see the failure, not swallow it.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "PlanMeter: unhandled UI-thread exception on the Dispatcher.");
            args.Handled = false;
        };

        // Non-UI thread: any unhandled exception domain-wide lands here as the process is
        // going down. Flush the rolling-file sink synchronously so the line is on disk
        // before exit. ExceptionObject is object? — it is usually an Exception but is not
        // guaranteed to be one.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "PlanMeter: unhandled exception on a non-UI thread (process will terminate).");
            }
            else
            {
                Log.Fatal("PlanMeter: unhandled exception on a non-UI thread (process will terminate). ExceptionObject: {ExceptionObject}", args.ExceptionObject?.ToString() ?? "(null)");
            }
            Log.CloseAndFlush();
        };

        // SEC-04: configure Serilog with the RedactorSink wrapper BEFORE anything else,
        // so any log event generated during startup flows through token-shape redaction.
        // Local file logs only under %LOCALAPPDATA%\PlanMeter\logs (zero out-of-process data flow).
        string logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlanMeter", "logs");
        Directory.CreateDirectory(logDir);

        // SEC-03/CR-01 — install RedactingSink as a SINK WRAPPER around an inner file sink.
        // The RedactingSink rebuilds every LogEvent with a redacted exception tree before
        // forwarding; the output template's {Exception} formatter calls ex.ToString() and
        // would otherwise emit the raw outer + inner exception Message text (e.g. a
        // rewrapped HttpRequestException whose inner message echoes a URL or response body
        // containing a Bearer token). The RedactingEnricher above cannot rewrite the
        // exception object — only a sink wrapper can. See RedactorSink.cs xml-doc for the
        // full threat model.
        //
        // Rolling/retention: Serilog.Sinks.File's public FileSink ctor is marked obsolete
        // (the library prefers the WriteTo.File(...) fluent config), and the only public
        // non-obsolete way to compose a wrapping sink with the fluent file config is via
        // WriteTo.Async(...) from a separate Serilog.Sinks.Async package, which the project
        // does not reference. Rather than add a new package dependency for a single
        // SEC-03 backstop, we deliberately accept the obsolete FileSink ctor here (the
        // obsolete-ness is a library-maintenance signal about the public API surface, NOT
        // about correctness — the FileSink itself is fully functional) and suppress the
        // CS0618 warning at the call site. The rolling behaviour is implemented manually:
        // a date-stamped path is computed each time the App starts, so each calendar day
        // produces a separate file (preserving the rolling-intent of the prior config).
        // The 7-day retention is best-effort: the directory is cleaned up on startup.
        string todayLogPath = Path.Combine(logDir, $"planmeter-{DateTime.UtcNow:yyyyMMdd}.log");
        CleanUpOldLogs(logDir, retentionDays: 7);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.With<RedactingEnricher>()
            .WriteTo.Sink(
                new RedactingSink(
#pragma warning disable CS0618 // Serilog.Sinks.File FileSink ctor is marked obsolete but is the only public non-Async-package way to wrap a file sink. See note above.
                    new Serilog.Sinks.File.FileSink(
                        todayLogPath,
                        new Serilog.Formatting.Display.MessageTemplateTextFormatter(
                            "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                            System.Globalization.CultureInfo.InvariantCulture),
                        fileSizeLimitBytes: null)))
#pragma warning restore CS0618
            .CreateLogger();

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                // SEC-02 + SEC-03 + Polly on the named "zai" HttpClient (unchanged).
                services.AddPlanMeterZaiClient();

                // SEC-02 + SEC-03 + Polly on the named "minimax" HttpClient (04-03 BRANCH A).
                services.AddPlanMeterMinimaxClient();

                // SEC-02 + SEC-03 + Polly on the named "opencode" HttpClient (08-01 OPC-02 —
                // the Phase-7 allow-list capability becomes traffic).
                services.AddPlanMeterOpenCodeClient();

                // SEC-02 + SEC-03 + Polly on the named "codex" HttpClient (09-01 CODEX-02 —
                // the host is already on the six-host allow-list; this does not grow it).
                services.AddPlanMeterCodexClient();

                // SEC-02 + SEC-03 + Polly on the named grok-auth + grok-billing clients
                // (10-01 GROK-03 — both hosts are already on the six-host allow-list;
                // these calls must not grow it).
                services.AddPlanMeterGrokAuthClient();
                services.AddPlanMeterGrokBillingClient();

                // Manual Z.ai key store (DPAPI).
                services.AddSingleton<DpapiKeyStore>();

                // D-07 — the per-provider DPAPI store factory the settings window (03-02)
                // resolves per provider card: keyStoreFactory(id) → DpapiKeyStore.ForProvider(id).
                // Registered as the method group so the per-provider entropy/blob derivation
                // lives in exactly one place (Core). SEC-04: keys stay in per-provider blobs,
                // never in config.json.
                services.AddSingleton<Func<ProviderId, DpapiKeyStore>>(_ => DpapiKeyStore.ForProvider);

                // SC#4 — the 60s global manual-refresh throttle (D-02/D-03/D-20). One
                // shared component consulted by the widget-global 'Refresh now' handler;
                // the save-key path (D-04) bypasses it.
                services.AddSingleton<GlobalRefreshGate>();

                // D-19/REFRESH-01 — the conservative refresh-backbone config, read ONCE at
                // startup (env > config.json > 600s default), clamped to >= 5 min in Release
                // with a logged clamp (SC#3). debugBuild is compile-gated: ONLY a Debug build
                // may honor the PLANMETER_UNSAFE_MIN_INTERVAL bypass — Release passes false
                // and the floor is absolute. The lazy factory resolves the host logger (the
                // Serilog-backed ILogger) so the clamp/startup lines land in the app log.
                services.AddSingleton(sp =>
                {
#if DEBUG
                    const bool debugBuild = true;
#else
                    const bool debugBuild = false;
#endif
                    return PlanMeterConfigLoader.Load(
                        debugBuild: debugBuild,
                        logger: sp.GetService<ILogger<PlanMeterConfig>>());
                });

                // D-15 — the read/write ConfigStore ({ pollIntervalSeconds, enabledProviders }
                // at %LOCALAPPDATA%\PlanMeter\config.json, atomic temp+move write). Registered
                // as a singleton so the App DI factory (enabled-set seeding below) and the
                // settings window (toggle persistence, 03-03 Task 2) share ONE instance.
                // NON-static sealed class with INSTANCE TryRead/Write/DefaultConfigPath — a
                // static class cannot be AddSingleton'd (CS0718) nor ctor-injected (CS0721).
                services.AddSingleton<ConfigStore>();

                // D-13/D-14 (03-04) — the shared, SINGLE-GLOBAL interval source. Seeded from
                // the STARTUP-resolved, clamped PlanMeterConfig.IntervalSeconds (env > file >
                // default, D-12/D-15). EVERY poller reads the SAME instance per tick (D-14);
                // the settings window's Polling card SetInterval's it live (persisting via the
                // ConfigStore), so a change applies on the NEXT scheduled poll without a
                // restart (SC#3). No event — ComputeNextDelay re-reads the volatile field per
                // tick. The STALE threshold (UsageStore.Interval) is NOT coupled to this value
                // (Pitfall 6).
                services.AddSingleton(sp =>
                    new PollIntervalSource(TimeSpan.FromSeconds(sp.GetRequiredService<PlanMeterConfig>().IntervalSeconds)));

                services.AddSingleton<BootShortcutManager>();

                // The reference adapter + the keyed observable store.
                //
                // SC#1 — the flat provider list is constructed EAGERLY (adapter ctors are
                // passive — no I/O at construction) so the hosted-service loop below can
                // enumerate it at registration time. The named-client factory is DEFERRED
                // (no service provider exists inside ConfigureServices): the deferring
                // shim is bound to the real factory between host Build and Start, before
                // any adapter can fetch. Adding a provider = ONE entry in this list; the
                // poller, the keyed store, and the UI are adapter-agnostic.
                _deferredHttpClientFactory = new DeferredHttpClientFactory();
                var grokTokenManager = new GrokTokenManager(_deferredHttpClientFactory);
                services.AddSingleton(grokTokenManager);
                var grokOAuthFlow = new GrokOAuthFlow(_deferredHttpClientFactory);
                services.AddSingleton(grokOAuthFlow);
                var providers = new List<IProviderAdapter>
                {
                    new ZaiAdapter(_deferredHttpClientFactory),
                    new GrokAdapter(_deferredHttpClientFactory, grokTokenManager),
                    new OpenCodeAdapter(_deferredHttpClientFactory),
                    new MinimaxAdapter(_deferredHttpClientFactory),
                    new CodexAdapter(_deferredHttpClientFactory),
                };
                services.AddSingleton<ZaiAdapter>(_ => (ZaiAdapter)providers[0]);
                services.AddSingleton<GrokAdapter>(_ => (GrokAdapter)providers[1]);
                services.AddSingleton<OpenCodeAdapter>(_ => (OpenCodeAdapter)providers[2]);
                services.AddSingleton<MinimaxAdapter>(_ => (MinimaxAdapter)providers[3]);
                services.AddSingleton<CodexAdapter>(_ => (CodexAdapter)providers[4]);
                services.AddSingleton<IReadOnlyList<IProviderAdapter>>(_ => providers);
                services.AddSingleton<UsageStore>();

                services.AddSingleton<ProviderRegistry>(sp =>
                {
                    var providers = sp.GetRequiredService<IReadOnlyList<IProviderAdapter>>();
                    // D-05/D-15 — seed the enabled-set from config.json at startup. A missing
                    // enabledProviders field (old shape / no file) → null → all enabled.
                    // Providers not registered (e.g. the Demo absent in Release) are ignored
                    // by the set.
                    var configStore = sp.GetRequiredService<ConfigStore>();
                    ConfigData? startup = configStore.TryRead(configStore.DefaultConfigPath);
                    return new ProviderRegistry(providers, startup?.EnabledProviders);
                });

                // D-15 — one poller per USAGE-API provider, each with its own credential
                // source, store slot, and timer (structural isolation, SC#2). Credential
                // selection is manifest-driven (Pitfall 6): a manual-key provider
                // (RequiresManualKey) binds its per-provider DPAPI store; a session
                // provider resolves through the ProviderId-keyed session-source lookup;
                // anything else falls back to the no-key source. Static rows
                // (SupportsUsageApi == false, D-08) still Register their store slot (the
                // row renders terminal at startup) but construct NO poller — a static row
                // can never make recurring authenticated calls.
                services.AddSingleton<IReadOnlyList<ProviderPoller>>(sp =>
                {
                    var store = sp.GetRequiredService<UsageStore>();
                    var keyStoreFactory = sp.GetRequiredService<Func<ProviderId, DpapiKeyStore>>();
                    var logger = sp.GetService<ILogger<ProviderPoller>>();
                    // REFRESH-01/adjacency — a SINGLE global interval applied to every
                    // provider's poller; there is no per-provider interval.
                    // The value is already clamped to >= 5 min by PlanMeterConfigLoader.
                    // D-13/D-14 (03-04) — ALL pollers share ONE PollIntervalSource instance
                    // (registered above, seeded from the startup PlanMeterConfig);
                    // ComputeNextDelay re-reads its Current per tick, so a settings-window
                    // interval change applies on the next scheduled poll without a restart.
                    var intervalSource = sp.GetRequiredService<PollIntervalSource>();
                    var registry = sp.GetRequiredService<ProviderRegistry>();
                    // The session-credential lookup (Pitfall 6 seam): providers whose
                    // credentials live in a local login session (rather than a manual key)
                    // resolve their ICredentialSource here. The grok slot is the park
                    // discriminator — it must stay bound (Pitfall 3).
                    var grokAuthSource = new GrokCredentialSource(sp.GetRequiredService<GrokTokenManager>());
                    var codexAuthSource = new CodexAuthJsonSource();
                    var sessionSources = new Dictionary<ProviderId, ICredentialSource>
                    {
                        { "grok", grokAuthSource },
                        { "codex", codexAuthSource },
                    };
                    var pollers = new List<ProviderPoller>();
                    foreach (var adapter in sp.GetRequiredService<IReadOnlyList<IProviderAdapter>>())
                    {
                        store.Register(adapter.Id);

                        // D-08 — a static row never polls: its store slot exists (terminal
                        // render at startup) but NO ProviderPoller is constructed.
                        if (!adapter.SupportsUsageApi)
                        {
                            continue;
                        }

                        // Manifest-driven credential selection (Pitfall 6): the manual-key
                        // flag binds the per-provider DPAPI store; session providers resolve
                        // through the lookup; everything else is keyless.
                        ICredentialSource keySource = adapter.RequiresManualKey
                            ? keyStoreFactory(adapter.Id)
                            : sessionSources.TryGetValue(adapter.Id, out var sessionSource)
                                ? sessionSource
                                : new NullCredentialSource();
                        var poller = new ProviderPoller(adapter, keySource, store, intervalSource, logger);
                        // D-05 — reconcile the poller's initial enabled-flag with the
                        // registry's startup enabled-set: a provider disabled in config.json
                        // must NOT poll after restart (its poller parks from the first tick;
                        // the startup fetch gate skips it).
                        poller.SetEnabled(registry.IsEnabled(adapter.Id));
                        pollers.Add(poller);
                    }

                    return pollers;
                });

                // G-04-4 root cause — AddHostedService(sp => ...) uses TryAddEnumerable
                // internally. Factory-form registrations carry ImplementationType=null,
                // so every registration after the FIRST is silently dropped as a duplicate.
                // Only the zai poller ever started; minimax's ExecuteAsync never ran.
                //
                // Fix: use plain ServiceDescriptor.Singleton<IHostedService> (which calls
                // ServiceCollection.Add, not TryAddEnumerable) so every provider gets
                // its own IHostedService registration regardless of ImplementationType.
                //
                // NEVER use the generic AddHostedService<T>() form for a poller — that
                // registers T itself as the implementation type, which would conflict
                // with the singleton registration above. One Add per POLLING provider,
                // resolved by provider id (never a fixed index): the loop scales to N
                // providers with no per-index wiring (Pitfall 6). A static row (D-08)
                // constructs no poller, so its id-based lookup finds nothing and
                // registers nothing — the loop naturally skips it.
                foreach (var provider in providers)
                {
                    if (!provider.SupportsUsageApi)
                    {
                        continue;
                    }

                    ProviderId pollerId = provider.Id;
                    services.Add(ServiceDescriptor.Singleton<IHostedService>(sp =>
                        sp.GetRequiredService<IReadOnlyList<ProviderPoller>>().First(p => p.Id == pollerId)));
                }
            })
            .Build();

        // Bind the deferred factory shim to the real named-client factory BEFORE the host
        // starts (no adapter has fetched yet — adapters fetch only from poller execution,
        // which begins at StartAsync), so the eagerly-constructed Z.ai adapter resolves the
        // real "zai" HttpClient on its first fetch.
        _deferredHttpClientFactory!.Bind(_host.Services.GetRequiredService<System.Net.Http.IHttpClientFactory>());

        await _host.StartAsync();

        var registry = _host.Services.GetRequiredService<ProviderRegistry>();
        var pollers = _host.Services.GetRequiredService<IReadOnlyList<ProviderPoller>>();
        var store = _host.Services.GetRequiredService<UsageStore>();
        var refreshGate = _host.Services.GetRequiredService<GlobalRefreshGate>();
        var keyStoreFactory = _host.Services.GetRequiredService<Func<ProviderId, DpapiKeyStore>>();
        var configStore = _host.Services.GetRequiredService<ConfigStore>();
        var intervalSource = _host.Services.GetRequiredService<PollIntervalSource>();
        var grokOAuthFlow = _host.Services.GetRequiredService<GrokOAuthFlow>();
        var grokTokenManager = _host.Services.GetRequiredService<GrokTokenManager>();
        var bootShortcuts = _host.Services.GetRequiredService<BootShortcutManager>();

        // Show the focus-safe topmost shell. MainWindow takes the generalized services.
        var window = new MainWindow(registry, pollers, store, keyStoreFactory, refreshGate, configStore, intervalSource, grokOAuthFlow, grokTokenManager, bootShortcuts);
        MainWindow = window;
        window.Show();

        StartBringToFrontWatcher();
        StartFullscreenWatcher();
    }

    /// <summary>
    /// WIDGET-04 — the first-instance watcher. Blocks on the named bring-forward handle;
    /// when a second instance signals it, re-asserts topmost on the existing window with
    /// No focus-activating calls (the bring-forward never steals keyboard focus —
    /// WIDGET-01 + this plan's prohibition). On exit the CTS is cancelled and the handle
    /// disposed BEFORE this background task observes it, so a pending <see cref="EventWaitHandle.WaitOne()"/>
    /// wakes and the loop exits cleanly; the <see cref="ObjectDisposedException"/> catch
    /// guards the dispose race so the background task never surfaces a noisy non-fatal
    /// exception on quit.
    /// </summary>
    private void StartBringToFrontWatcher()
    {
        var handle = _bringToFrontHandle;
        var cts = _bringToFrontCts;
        if (handle is null || cts is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    if (!handle.WaitOne())
                    {
                        break;
                    }
                }
                catch (ObjectDisposedException)
                {
                    // handle disposed on shutdown — normal exit.
                    break;
                }

                try
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (MainWindow is not null)
                        {
                            MainWindow.Show();
                            MainWindow.Topmost = true;
                            WindowExtensions.ApplyTopmostNoActivate(MainWindow);
                        }
                    });
                }
                catch (ObjectDisposedException)
                {
                    // handle disposed on shutdown — normal exit.
                }
            }
        });
    }

    /// <summary>
    /// WIDGET-05b — fullscreen auto-hide watcher. Polls <c>FullscreenDetector.IsForegroundFullscreen()</c>
    /// at 2s intervals via a <c>PeriodicTimer</c>. Marshals <c>Visibility</c> changes to the UI
    /// thread via <c>Dispatcher.InvokeAsync</c> — NEVER <c>Show()/Hide()/Activate()</c> (D-10).
    /// On any P/Invoke exception, defaults to <c>Visibility.Visible</c> (fail-open, UI-SPEC H3).
    /// Mirrors the <c>StartBringToFrontWatcher()</c> manual Task.Run pattern.
    /// </summary>
    private void StartFullscreenWatcher()
    {
        var cts = _fullscreenCts;
        if (cts is null)
        {
            return;
        }

        #pragma warning disable CS4014
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            bool wasFullscreen = false;

            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await timer.WaitForNextTickAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                bool isFullscreen;
                try
                {
                    isFullscreen = FullscreenDetector.IsForegroundFullscreen();
                }
                catch (Exception ex)
                {
                    // Fail-open: on any P/Invoke failure, default to Visible.
                    // Better to overlap a game for 2s than to disappear permanently (UI-SPEC H3).
                    Log.Warning(ex, "PlanMeter: fullscreen detector P/Invoke failed, defaulting to visible.");
                    isFullscreen = false;
                }

                // Only marshal on state change to avoid unnecessary UI-thread work.
                if (isFullscreen == wasFullscreen)
                {
                    continue;
                }
                wasFullscreen = isFullscreen;

                try
                {
                    var newVisibility = isFullscreen ? Visibility.Collapsed : Visibility.Visible;
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (MainWindow is not null)
                        {
                            MainWindow.Visibility = newVisibility;
                        }
                    });
                }
                catch (ObjectDisposedException)
                {
                    // Dispatcher shut down — normal exit.
                }
            }
        });
#pragma warning restore CS4014
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // WIDGET-05b — cancel the fullscreen watcher alongside the bring-forward watcher.
        try { _fullscreenCts?.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }

        // WIDGET-04 — cancel the bring-forward watcher BEFORE disposing the handle so a
        // pending WaitOne() wakes and the loop exits cleanly (cancel-then-dispose); the
        // ObjectDisposedException catch inside the watcher guards the dispose race so the
        // background task never surfaces a noisy non-fatal exception on quit.
        try { _bringToFrontCts?.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }
        try { _bringToFrontHandle?.Dispose(); } catch (ObjectDisposedException) { /* already disposed */ }

        if (_host is not null)
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(2));
                _host.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "PlanMeter: host stop threw on exit.");
            }
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>
    /// A deferring <see cref="System.Net.Http.IHttpClientFactory"/> shim: adapters are
    /// constructed eagerly inside ConfigureServices (before any service provider exists),
    /// so the real factory is bound immediately after host Build. CreateClient throws if
    /// called before the bind — which can never happen, because adapters fetch only from
    /// poller execution and the settings-window save path, both of which start after
    /// host StartAsync.
    /// </summary>
    private sealed class DeferredHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        private volatile System.Net.Http.IHttpClientFactory? _real;

        public void Bind(System.Net.Http.IHttpClientFactory real) => _real = real;

        public System.Net.Http.HttpClient CreateClient(string name)
        {
            var real = _real;
            if (real is null)
            {
                throw new InvalidOperationException(
                    "The named-client factory was used before the host was built — an adapter fetched during DI construction.");
            }

            return real.CreateClient(name);
        }
    }

    /// <summary>
    /// Best-effort rolling-log retention. Deletes any <c>planmeter-YYYYMMDD.log</c> files
    /// older than <paramref name="retentionDays"/> from <paramref name="logDir"/>. Called
    /// once at startup (CR-01 replaced Serilog's built-in RollingInterval.Day with a
    /// startup-stamped path so the wrapping RedactingSink could be installed — retention
    /// is now this method's responsibility). Swallows IOException — log cleanup is
    /// best-effort and must never crash the app.
    /// </summary>
    private static void CleanUpOldLogs(string logDir, int retentionDays)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            foreach (var file in Directory.EnumerateFiles(logDir, "planmeter-*.log"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                // planmeter-YYYYMMDD → YYYYMMDD
                if (name.Length < "planmeter-".Length + 8) continue;
                string datePart = name.Substring(name.Length - 8);
                if (DateTime.TryParseExact(
                        datePart,
                        "yyyyMMdd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var fileDate)
                    && fileDate < cutoff)
                {
                    try { File.Delete(file); } catch { /* best-effort */ }
                }
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; do not crash startup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; do not crash startup.
        }
    }

}
