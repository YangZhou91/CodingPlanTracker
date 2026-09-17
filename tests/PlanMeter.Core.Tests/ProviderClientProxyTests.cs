using System;
using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PlanMeter.Core.Http;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// Regression: aeb2606 set <c>UseProxy=false</c> on every named provider client to
/// unstick Z.ai through Clash. chatgpt.com / grok.com / auth.x.ai / opencode.ai
/// TCP-timeout on direct IPv4 from this network and must hop through the live
/// <see cref="ProxySource"/> (explicit host:port, default 127.0.0.1:7897 — not the
/// WinINET system proxy).
/// Oracle: specified (per-client UseProxy + Proxy identity contract).
/// </summary>
public sealed class ProviderClientProxyTests
{
    [Theory]
    [InlineData(HttpExtensions.ZaiClientName, false)]
    [InlineData(HttpExtensions.MinimaxClientName, false)]
    [InlineData(HttpExtensions.CodexClientName, true)]
    [InlineData(HttpExtensions.GrokAuthClientName, true)]
    [InlineData(HttpExtensions.GrokBillingClientName, true)]
    [InlineData(HttpExtensions.OpenCodeClientName, true)]
    public void Named_provider_client_UseProxy_matches_direct_vs_proxied_hosts(
        string clientName,
        bool expectedUseProxy)
    {
        using var provider = BuildAllClients();
        SocketsHttpHandler primary = GetPrimaryHandler(provider, clientName);
        var source = provider.GetRequiredService<ProxySource>();

        primary.UseProxy.Should().Be(expectedUseProxy,
            "{0} must {1} the explicit ProxySource hop",
            clientName,
            expectedUseProxy ? "use" : "bypass");
        primary.ConnectCallback.Should().NotBeNull(
            "IPv4 ConnectCallback stays on every provider client");

        if (expectedUseProxy)
        {
            primary.Proxy.Should().BeSameAs(source,
                "{0} must send through the DI ProxySource singleton so Settings Set is live",
                clientName);
        }
        else
        {
            primary.Proxy.Should().BeNull(
                "{0} stays direct IPv4 — Clash hung api.z.ai",
                clientName);
        }
    }

    [Fact]
    public void Proxied_named_clients_share_the_same_ProxySource_singleton()
    {
        using var provider = BuildAllClients();
        var source = provider.GetRequiredService<ProxySource>();

        GetPrimaryHandler(provider, HttpExtensions.CodexClientName).Proxy.Should().BeSameAs(source);
        GetPrimaryHandler(provider, HttpExtensions.GrokAuthClientName).Proxy.Should().BeSameAs(source);
        GetPrimaryHandler(provider, HttpExtensions.GrokBillingClientName).Proxy.Should().BeSameAs(source);
        GetPrimaryHandler(provider, HttpExtensions.OpenCodeClientName).Proxy.Should().BeSameAs(source);
        GetPrimaryHandler(provider, HttpExtensions.ZaiClientName).Proxy.Should().BeNull();
        GetPrimaryHandler(provider, HttpExtensions.MinimaxClientName).Proxy.Should().BeNull();
    }

    [Fact]
    public void Params_overload_defaults_UseProxy_true()
    {
        var services = new ServiceCollection();
        services.AddPlanMeterProviderClient(
            "testprov", "https://api.z.ai/", TimeSpan.FromSeconds(15), "api.z.ai");

        using var provider = services.BuildServiceProvider();
        SocketsHttpHandler handler = GetPrimaryHandler(provider, "testprov");
        handler.UseProxy.Should().BeTrue(
            "the params-only overload must default to the explicit ProxySource hop so CN-blocked hosts keep working");
        handler.Proxy.Should().BeSameAs(provider.GetRequiredService<ProxySource>());
    }

    [Fact]
    public void Explicit_useProxy_false_disables_proxy_and_leaves_Proxy_null()
    {
        var services = new ServiceCollection();
        services.AddPlanMeterProviderClient(
            "testprov", "https://api.z.ai/", TimeSpan.FromSeconds(15), useProxy: false, "api.z.ai");

        using var provider = services.BuildServiceProvider();
        SocketsHttpHandler handler = GetPrimaryHandler(provider, "testprov");
        handler.UseProxy.Should().BeFalse(
            "useProxy:false must stick on the primary SocketsHttpHandler (Z.ai/MiniMax path)");
        handler.Proxy.Should().BeNull(
            "direct clients must not carry a Proxy instance");
    }

    private static ServiceProvider BuildAllClients()
    {
        var services = new ServiceCollection();
        services.AddPlanMeterZaiClient();
        services.AddPlanMeterMinimaxClient();
        services.AddPlanMeterCodexClient();
        services.AddPlanMeterGrokAuthClient();
        services.AddPlanMeterGrokBillingClient();
        services.AddPlanMeterOpenCodeClient();
        return services.BuildServiceProvider();
    }

    private static SocketsHttpHandler GetPrimaryHandler(IServiceProvider provider, string clientName)
    {
        var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();
        HttpMessageHandler current = factory.CreateHandler(clientName);
        while (current is DelegatingHandler delegating && delegating.InnerHandler is not null)
        {
            current = delegating.InnerHandler;
        }

        return current.Should().BeOfType<SocketsHttpHandler>(
            "the innermost handler must be the pinned SocketsHttpHandler").Subject;
    }
}
