using System.Collections.Generic;
using FluentAssertions;
using PlanMeter.Core.Adapters;
using PlanMeter.Core.Models;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// SC#1 — the ProviderRegistry contract: the flat adapter list enumerates in
/// registration order and resolves by <see cref="ProviderId"/>. Adding a provider =
/// one entry in the list (never a scheduler/store/UI edit).
/// </summary>
public sealed class ProviderRegistryTests
{
    [Fact]
    public void Enabled_enumerates_adapters_in_registration_order()
    {
        var registry = new ProviderRegistry(new IProviderAdapter[]
        {
            new StubAdapter(),
        });

        registry.Enabled.Should().ContainSingle();
        registry.Enabled[0].Id.Should().Be(new ProviderId("stub"));
    }

    [Fact]
    public void Enabled_preserves_multi_adapter_registration_order()
    {
        // Z.ai first, then the stub — the stable row order the UI renders (PROV-02/ordering).
        var zai = new ZaiAdapter(new StubFactory(new StubHandler(new System.Net.Http.HttpResponseMessage(
            System.Net.HttpStatusCode.OK))));
        var registry = new ProviderRegistry(new IProviderAdapter[]
        {
            zai,
            new StubAdapter(),
        });

        registry.Enabled.Count.Should().Be(2);
        registry.Enabled[0].Id.Should().Be(new ProviderId("zai"));
        registry.Enabled[1].Id.Should().Be(new ProviderId("stub"));
    }

    [Fact]
    public void Get_returns_the_adapter_with_the_given_id()
    {
        var zai = new ZaiAdapter(new StubFactory(new StubHandler(new System.Net.Http.HttpResponseMessage(
            System.Net.HttpStatusCode.OK))));
        var registry = new ProviderRegistry(new IProviderAdapter[] { zai, new StubAdapter() });

        registry.Get(new ProviderId("zai")).Should().BeSameAs(zai);
        registry.Get("stub").Should().BeOfType<StubAdapter>();
    }

    [Fact]
    public void Get_unknown_id_returns_null()
    {
        var registry = new ProviderRegistry(new IProviderAdapter[] { new StubAdapter() });

        registry.Get(new ProviderId("nope")).Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // D-03 — the mutable enabled-set contract.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsEnabled_defaults_to_true_for_every_registered_provider()
    {
        var zai = new ZaiAdapter(new StubFactory(new StubHandler(new System.Net.Http.HttpResponseMessage(
            System.Net.HttpStatusCode.OK))));
        var registry = new ProviderRegistry(new IProviderAdapter[] { zai, new StubAdapter() });

        registry.IsEnabled("zai").Should().BeTrue("the default enabled-set is all-enabled");
        registry.IsEnabled("stub").Should().BeTrue();
    }

    [Fact]
    public void All_exposes_every_registered_adapter_regardless_of_state()
    {
        var zai = new ZaiAdapter(new StubFactory(new StubHandler(new System.Net.Http.HttpResponseMessage(
            System.Net.HttpStatusCode.OK))));
        var registry = new ProviderRegistry(new IProviderAdapter[] { zai, new StubAdapter() });

        registry.SetEnabled("zai", false);

        // All = the FULL list (enabled + disabled) — the settings window iterates this so
        // a fully-disabled provider stays reachable for re-enabling (D-03 / add-alongside).
        registry.All.Select(a => a.Id).Should().Equal(new ProviderId("zai"), new ProviderId("stub"));
        registry.All.Count.Should().Be(2);
    }

    [Fact]
    public void Enabled_filters_by_the_enabled_set_and_preserves_registration_order()
    {
        var zai = new ZaiAdapter(new StubFactory(new StubHandler(new System.Net.Http.HttpResponseMessage(
            System.Net.HttpStatusCode.OK))));
        var registry = new ProviderRegistry(new IProviderAdapter[] { zai, new StubAdapter() });

        // Disable Z.ai → Enabled drops it, the stub remains.
        registry.SetEnabled("zai", false);
        registry.Enabled.Select(a => a.Id).Should().Equal(new ProviderId("stub"));

        // Re-enable → Z.ai returns, and the order is the REGISTRATION order (zai, stub) —
        // a disabled-then-restored adapter must not re-sort (PROV-02/ordering).
        registry.SetEnabled("zai", true);
        registry.Enabled.Select(a => a.Id).Should().Equal(new ProviderId("zai"), new ProviderId("stub"));
    }

    [Fact]
    public void SetEnabled_fires_EnabledChanged_with_the_id_and_new_state_only_on_actual_change()
    {
        var zai = new ZaiAdapter(new StubFactory(new StubHandler(new System.Net.Http.HttpResponseMessage(
            System.Net.HttpStatusCode.OK))));
        var registry = new ProviderRegistry(new IProviderAdapter[] { zai, new StubAdapter() });
        var events = new System.Collections.Generic.List<ProviderEnabledChangedEventArgs>();
        registry.EnabledChanged += (_, e) => events.Add(e);

        registry.SetEnabled("zai", false);
        registry.SetEnabled("zai", false); // idempotent — a second disable is a NO-OP (no event)

        events.Should().HaveCount(1, "an idempotent SetEnabled must not fire a second event");
        events[0].Id.Should().Be(new ProviderId("zai"));
        events[0].Enabled.Should().BeFalse();

        registry.SetEnabled("zai", true);
        events.Should().HaveCount(2);
        events[1].Id.Should().Be(new ProviderId("zai"));
        events[1].Enabled.Should().BeTrue();
    }

    [Fact]
    public void Constructor_seeds_the_enabled_set_from_given_ids()
    {
        var zai = new ZaiAdapter(new StubFactory(new StubHandler(new System.Net.Http.HttpResponseMessage(
            System.Net.HttpStatusCode.OK))));
        // Seed with ONLY the stub enabled (the D-05 startup shape: config.json enabledProviders).
        var registry = new ProviderRegistry(
            new IProviderAdapter[] { zai, new StubAdapter() },
            enabled: new[] { new ProviderId("stub") });

        registry.IsEnabled("stub").Should().BeTrue();
        registry.IsEnabled("zai").Should().BeFalse();
        registry.Enabled.Should().ContainSingle();
        registry.Enabled[0].Id.Should().Be(new ProviderId("stub"));
    }

    [Fact]
    public void Constructor_ignores_seed_ids_that_are_not_registered()
    {
        // A provider absent from the registration list (e.g. a provider gated out of a Release
        // build) is silently dropped from the set — and a seed made up SOLELY of unregistered
        // ids leaves every registered provider disabled (D-05: unregistered ids are ignored,
        // never stored; the enabled-set reflects exactly the registered ∩ seeded ids).
        var zai = new ZaiAdapter(new StubFactory(new StubHandler(new System.Net.Http.HttpResponseMessage(
            System.Net.HttpStatusCode.OK))));
        var registry = new ProviderRegistry(
            new IProviderAdapter[] { zai },
            enabled: new[] { new ProviderId("stub") });

        registry.IsEnabled("stub").Should().BeFalse("an unregistered id is ignored by the set");
        registry.IsEnabled("zai").Should().BeFalse("a seed made up solely of unregistered ids enables nothing");
        registry.Enabled.Should().BeEmpty();
    }

    /// <summary>
    /// A minimal local fake adapter (id "stub") — keyless, supports the usage API, always
    /// detected, returns a fixed Ok reading. Replaces the retired dev-only second adapter
    /// in every ordering/enabled-set assertion shape (the registry tests exercise the
    /// REGISTRY contract, not any real provider).
    /// </summary>
    private sealed class StubAdapter : IProviderAdapter
    {
        public ProviderId Id => "stub";
        public string DisplayName => "Stub";
        public bool SupportsUsageApi => true;
        public bool RequiresManualKey => false;
        public bool ManualOnlyFetch => false;
        public bool SupportsOAuthLogin => false;
        public AuthFamily AuthFamily => AuthFamily.Key;
        public string? ConsoleUrl => null;
        public string? QualifierText => null;
        public string? UnsupportedReason => null;
        public string? FloorReason => null;
        public string? ReLoginGuidance => null;
        public bool Detect() => true;

        public Task<UsageReading> TestFetchAsync(string? apiKey, System.Threading.CancellationToken ct = default)
            => Task.FromResult(FixedReading());

        public Task<AdapterFetchResult> FetchUsageAsync(string? apiKey, System.Threading.CancellationToken ct = default)
            => Task.FromResult(new AdapterFetchResult(FixedReading(), null));

        private static UsageReading FixedReading() => new(
            Provider: "Stub",
            FetchedAtUtc: DateTimeOffset.UtcNow,
            Status: ReadingStatus.Ok,
            UsedPct: 50.0,
            RemainingPct: 50.0,
            MostBindingWindow: WindowKind.FiveHour,
            AllWindows: null,
            ErrorMessage: null);
    }

    private sealed class StubHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly System.Net.Http.HttpResponseMessage _response;
        public StubHandler(System.Net.Http.HttpResponseMessage response) { _response = response; }

        protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken)
            => System.Threading.Tasks.Task.FromResult(_response);
    }

    private sealed class StubFactory : System.Net.Http.IHttpClientFactory
    {
        private readonly System.Net.Http.HttpMessageHandler _handler;
        public StubFactory(System.Net.Http.HttpMessageHandler handler) { _handler = handler; }
        public System.Net.Http.HttpClient CreateClient(string name)
            => new(_handler, disposeHandler: false);
    }
}
