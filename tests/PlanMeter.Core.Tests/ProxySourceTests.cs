using System;
using System.Net;
using FluentAssertions;
using PlanMeter.Core.Http;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// Live <see cref="ProxySource"/> contract: default Clash mixed-port, Seed never throws,
/// TryParse host:port / http://host:port, Set mutates the same IWebProxy instance.
/// </summary>
public sealed class ProxySourceTests
{
    private static readonly Uri Destination = new("https://chatgpt.com/backend-api/wham/usage");

    [Fact]
    public void Default_Enabled_is_true_and_Address_is_clash_mixed_port()
    {
        var source = new ProxySource();

        source.Enabled.Should().BeTrue();
        source.Address.Should().Be("127.0.0.1:7897");
        source.Address.Should().Be(ProxySource.DefaultAddress);
        source.GetProxy(Destination).Should().Be(new Uri("http://127.0.0.1:7897/"));
        source.IsBypassed(Destination).Should().BeFalse();
        source.Credentials.Should().BeNull();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "")]
    [InlineData(null, "   ")]
    [InlineData(true, null)]
    [InlineData(false, "")]
    [InlineData(false, "   ")]
    public void Seed_null_or_whitespace_degrades_to_defaults(bool? useProxy, string? address)
    {
        var source = new ProxySource();
        source.Set(false, "10.0.0.1:7890");

        source.Invoking(s => s.Seed(useProxy, address)).Should().NotThrow();

        source.Enabled.Should().BeTrue();
        source.Address.Should().Be(ProxySource.DefaultAddress);
        source.GetProxy(Destination).Should().Be(new Uri("http://127.0.0.1:7897/"));
        source.IsBypassed(Destination).Should().BeFalse();
    }

    [Theory]
    [InlineData("no-port")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:0")]
    [InlineData("127.0.0.1:65536")]
    [InlineData("[::1]:7897")]
    public void Seed_invalid_address_degrades_to_default_and_never_throws(string address)
    {
        var source = new ProxySource();
        source.Set(false, "10.0.0.1:7890");

        source.Invoking(s => s.Seed(false, address)).Should().NotThrow();

        source.Enabled.Should().BeTrue("invalid Seed must fall back to enabled + default, not the requested false");
        source.Address.Should().Be(ProxySource.DefaultAddress);
    }

    [Fact]
    public void Seed_valid_pair_applies_disabled_and_custom_address()
    {
        var source = new ProxySource();

        source.Invoking(s => s.Seed(false, "10.0.0.1:7890")).Should().NotThrow();

        source.Enabled.Should().BeFalse();
        source.Address.Should().Be("10.0.0.1:7890");
        source.GetProxy(Destination).Should().Be(new Uri("http://10.0.0.1:7890/"));
        source.IsBypassed(Destination).Should().BeTrue();
    }

    [Theory]
    [InlineData("127.0.0.1:7897", "127.0.0.1:7897", "http://127.0.0.1:7897/")]
    [InlineData("http://10.0.0.1:7890", "10.0.0.1:7890", "http://10.0.0.1:7890/")]
    [InlineData("http://10.0.0.1:7890/", "10.0.0.1:7890", "http://10.0.0.1:7890/")]
    [InlineData("localhost:8080", "localhost:8080", "http://localhost:8080/")]
    [InlineData("127.0.0.1:65535", "127.0.0.1:65535", "http://127.0.0.1:65535/")]
    [InlineData("127.0.0.1:1", "127.0.0.1:1", "http://127.0.0.1:1/")]
    public void TryParse_accepts_host_port_and_http_uri(string input, string display, string uri)
    {
        ProxySource.TryParse(input, out string parsedDisplay, out Uri parsedUri).Should().BeTrue();
        parsedDisplay.Should().Be(display);
        parsedUri.Should().Be(new Uri(uri));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("127.0.0.1")]
    [InlineData("http://127.0.0.1")]
    [InlineData("127.0.0.1:0")]
    [InlineData("127.0.0.1:65536")]
    [InlineData(":7897")]
    [InlineData("http://:7897")]
    [InlineData("[::1]:7897")]
    [InlineData("http://[::1]:7897")]
    [InlineData("https://127.0.0.1:7897")]
    public void TryParse_rejects_missing_port_out_of_range_empty_host_and_ipv6(string? input)
    {
        ProxySource.TryParse(input, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void GetProxy_returns_http_host_port_slash_and_IsBypassed_tracks_Enabled()
    {
        var source = new ProxySource();
        source.GetProxy(Destination).Should().Be(new Uri("http://127.0.0.1:7897/"));
        source.IsBypassed(Destination).Should().BeFalse("Enabled default is true — do not bypass");

        source.Set(false, "127.0.0.1:7897");

        source.Enabled.Should().BeFalse();
        source.IsBypassed(Destination).Should().BeTrue();
        source.GetProxy(Destination).Should().Be(new Uri("http://127.0.0.1:7897/"));
    }

    [Fact]
    public void Set_valid_address_updates_live_getters_on_the_same_instance()
    {
        var source = new ProxySource();
        IWebProxy asProxy = source;

        source.Set(true, "10.0.0.1:7890");

        source.Enabled.Should().BeTrue();
        source.Address.Should().Be("10.0.0.1:7890");
        source.GetProxy(Destination).Should().Be(new Uri("http://10.0.0.1:7890/"));
        source.IsBypassed(Destination).Should().BeFalse();
        ReferenceEquals(asProxy, source).Should().BeTrue("Settings mutates this instance; handlers must not see a new object");
        asProxy.GetProxy(Destination).Should().Be(new Uri("http://10.0.0.1:7890/"));
    }

    [Fact]
    public void Set_http_uri_form_stores_display_as_host_port()
    {
        var source = new ProxySource();

        source.Set(true, "http://10.0.0.1:7890");

        source.Address.Should().Be("10.0.0.1:7890");
        source.GetProxy(Destination).Should().Be(new Uri("http://10.0.0.1:7890/"));
    }

    [Fact]
    public void Set_invalid_when_enabled_throws_and_does_not_mutate()
    {
        var source = new ProxySource();

        source.Invoking(s => s.Set(true, "bad")).Should().Throw<ArgumentException>();

        source.Enabled.Should().BeTrue();
        source.Address.Should().Be(ProxySource.DefaultAddress);
        source.GetProxy(Destination).Should().Be(new Uri("http://127.0.0.1:7897/"));
    }

    [Fact]
    public void Set_invalid_nonempty_when_disabled_throws_and_does_not_mutate()
    {
        var source = new ProxySource();

        source.Invoking(s => s.Set(false, "no-port")).Should().Throw<ArgumentException>();

        source.Enabled.Should().BeTrue();
        source.Address.Should().Be(ProxySource.DefaultAddress);
    }

    [Fact]
    public void Set_disabled_with_empty_address_keeps_last_valid_address()
    {
        var source = new ProxySource();
        source.Set(true, "10.0.0.1:7890");

        source.Set(false, "");

        source.Enabled.Should().BeFalse();
        source.Address.Should().Be("10.0.0.1:7890");
        source.IsBypassed(Destination).Should().BeTrue();
        source.GetProxy(Destination).Should().Be(new Uri("http://10.0.0.1:7890/"));
    }
}
