using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CsharpBgapi.Tests;

public class CsharpBgapiServiceExtensionsTests
{
    // A host registers logging itself; AddCsharpBgapi does not, so a bare collection gets a null one.
    private static ServiceProvider Build(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        register(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddCsharpBgapi_RegistersBgapiDeviceAsASingleton()
    {
        using var provider = Build(s => s.AddCsharpBgapi());

        provider.GetRequiredService<BgapiDevice>().Should().BeSameAs(provider.GetRequiredService<BgapiDevice>());
    }

    [Fact]
    public void AddCsharpBgapi_ByDefault_LoadsTheBuiltInDefinitions()
    {
        using var provider = Build(s => s.AddCsharpBgapi());

        provider.GetRequiredService<BgapiDevice>().Definitions.LoadedApiNames.Should().BeEquivalentTo("bt", "btmesh");
    }

    [Fact]
    public void AddCsharpBgapi_LoadDefaultXapisFalse_LoadsNothing()
    {
        using var provider = Build(s => s.AddCsharpBgapi(loadDefaultXapis: false));

        provider.GetRequiredService<BgapiDevice>().Definitions.HasDefinitions.Should().BeFalse();
    }

    [Fact]
    public void AddCsharpBgapi_ConfigureDelegate_ReachesTheDevice()
    {
        using var provider = Build(s => s.AddCsharpBgapi(o => o.ResponseTimeoutSeconds = 7.5));

        provider.GetRequiredService<BgapiDevice>().ResponseTimeout.Should().Be(TimeSpan.FromSeconds(7.5));
    }

    // With no ILoggerFactory registered, both registrations fell back to BgapiDevice(ILogger?),
    // which builds default options, so the configure delegate was silently ignored — any host that
    // never called AddLogging got the default of every option.

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddCsharpBgapi_WithoutLogging_ConfigureDelegateStillReachesTheDevice(bool loadDefaultXapis)
    {
        var services = new ServiceCollection();
        services.AddCsharpBgapi(o => o.ResponseTimeoutSeconds = 7.5, loadDefaultXapis);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<BgapiDevice>().ResponseTimeout.Should().Be(TimeSpan.FromSeconds(7.5));
    }
}
