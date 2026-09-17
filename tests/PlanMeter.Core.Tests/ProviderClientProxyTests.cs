using System;
using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PlanMeter.Core.Http;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// Regression: aeb2606 set <c>UseProxy=false</c> on every named provider client to
/// unstick Z.ai through Clash. chatgpt.com / grok.com / auth.x.ai TCP-timeout on
/// direct IPv4 from this network and must keep the WinINET system proxy.
/// Oracle: specified (per-client UseProxy contract).
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

        primary.UseProxy.Should().Be(expectedUseProxy,
            "{0} must {1} the WinINET system proxy",
            clientName,
            expectedUseProxy ? "use" : "bypass");
        primary.ConnectCallback.Should().NotBeNull(
            "IPv4 ConnectCallback stays on every provider client");
    }

    [Fact]
    public void Params_overload_defaults_UseProxy_true()
    {
        var services = new ServiceCollection();
        services.AddPlanMeterProviderClient(
            "testprov", "https://api.z.ai/", TimeSpan.FromSeconds(15), "api.z.ai");

        using var provider = services.BuildServiceProvider();
        GetPrimaryHandler(provider, "testprov").UseProxy.Should().BeTrue(
            "the params-only overload must default to the system proxy so CN-blocked hosts keep working");
    }

    [Fact]
    public void Explicit_useProxy_false_disables_system_proxy()
    {
        var services = new ServiceCollection();
        services.AddPlanMeterProviderClient(
            "testprov", "https://api.z.ai/", TimeSpan.FromSeconds(15), useProxy: false, "api.z.ai");

        using var provider = services.BuildServiceProvider();
        GetPrimaryHandler(provider, "testprov").UseProxy.Should().BeFalse(
            "useProxy:false must stick on the primary SocketsHttpHandler (Z.ai/MiniMax path)");
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
