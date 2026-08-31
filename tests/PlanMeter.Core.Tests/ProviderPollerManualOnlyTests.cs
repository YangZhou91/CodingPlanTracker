using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using PlanMeter.Core.Adapters;
using PlanMeter.Core.Credentials;
using PlanMeter.Core.Models;
using PlanMeter.Core.Polling;
using PlanMeter.Core.Store;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// GRND-02/D-02 — runtime behavioral test proving a ManualOnlyFetch provider is
/// never ticked by the background timer and responds to RefreshNowAsync with
/// exactly one fetch.
/// </summary>
public sealed class ProviderPollerManualOnlyTests
{
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(25);
        }

        return true;
    }

    /// <summary>
    /// A ManualOnlyFetch adapter: its poller must never create a PeriodicTimer,
    /// never run a startup fetch, and never tick on the background timer.
    /// A RefreshNowAsync signal triggers exactly one fetch.
    /// </summary>
    [Fact]
    public async Task ManualOnly_provider_is_never_ticked_by_background_timer()
    {
        var adapter = new ManualOnlyFakeAdapter();
        var store = new UsageStore();
        store.Register("manualonly");
        var logger = new RecordingLogger();
        using var cts = new CancellationTokenSource();
        var poller = new ProviderPoller(
            adapter, new NullCredentialSource(), store,
            new PollIntervalSource(TimeSpan.FromMilliseconds(100)), logger);

        await poller.StartAsync(cts.Token);

        // Wait 800ms — 8 tick windows at 100ms. A non-manual poller would
        // fetch multiple times; a manual-only must fetch ZERO times.
        await Task.Delay(800);
        adapter.FetchCount.Should().Be(0,
            "a ManualOnlyFetch provider must NOT be ticked by the background timer");

        // Explicit RefreshNowAsync — must trigger exactly ONE fetch.
        var reading = await poller.RefreshNowAsync(cts.Token);
        adapter.FetchCount.Should().Be(1,
            "RefreshNowAsync must trigger exactly one fetch for a ManualOnly provider");
        reading.Status.Should().Be(ReadingStatus.Ok);
        store.Current("manualonly")!.UsedPct.Should().Be(50.0,
            "the fetched reading must be published to the store");

        // Wait another 800ms — no further fetches.
        await Task.Delay(800);
        adapter.FetchCount.Should().Be(1,
            "no further background ticks must fire after the manual fetch");

        await poller.StopAsync(cts.Token);
        poller.ExecuteTask!.IsFaulted.Should().BeFalse("the poller must not fault");

        logger.Lines.Should().Contain(l => l.Contains("poller manual-only park"),
            "the manual-only park log line must be emitted");
    }

    /// <summary>
    /// Fake adapter with ManualOnlyFetch = true. FetchUsageAsync includes a 100ms
    /// delay for R3 handshake realism and returns Ok(50%, 50%).
    /// </summary>
    private sealed class ManualOnlyFakeAdapter : IProviderAdapter
    {
        private int _fetchCount;

        public ProviderId Id => "manualonly";
        public string DisplayName => "ManualOnly";
        public bool SupportsUsageApi => true;
        public bool RequiresManualKey => false;
        public bool ManualOnlyFetch => true;
        public bool SupportsOAuthLogin => false;
        public AuthFamily AuthFamily => AuthFamily.Session;
        public string? ConsoleUrl => null;
        public string? QualifierText => null;
        public string? UnsupportedReason => null;
        public string? FloorReason => null;
        public string? ReLoginGuidance => null;
        public bool Detect() => true;
        public int FetchCount => Volatile.Read(ref _fetchCount);

        public Task<UsageReading> TestFetchAsync(string? apiKey, CancellationToken ct = default)
            => Task.FromResult(OkReading());

        public async Task<AdapterFetchResult> FetchUsageAsync(string? apiKey, CancellationToken ct = default)
        {
            // R3 handshake realism: a small delay so the in-flight slot is
            // observable by concurrent callers.
            await Task.Delay(100, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _fetchCount);
            return new AdapterFetchResult(OkReading(), null);
        }

        private static UsageReading OkReading() => new(
            Provider: "ManualOnly",
            FetchedAtUtc: DateTimeOffset.UtcNow,
            Status: ReadingStatus.Ok,
            UsedPct: 50.0,
            RemainingPct: 50.0,
            MostBindingWindow: default,
            AllWindows: null,
            ErrorMessage: null);
    }

    /// <summary>Records formatted log lines for assertion (same shape as
    /// ProviderPollerTests.RecordingLogger but typed generically).</summary>
    private sealed class RecordingLogger : ILogger<ProviderPoller>
    {
        public readonly System.Collections.Generic.List<string> Lines = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string line = formatter(state, exception);
            lock (Lines) Lines.Add(line);
        }
    }
}
