using FluentAssertions;
using SilabsBgapi.Protocol;
using Xunit;

namespace SilabsBgapi.Tests;

public class EmbeddedXapiTests
{
    [Fact]
    public void HasDefinitions_ShouldBeFalse_BeforeAnyLoad()
    {
        var definitions = new XapiDefinitions();
        definitions.HasDefinitions.Should().BeFalse();
    }

    [Fact]
    public void LoadedApiNames_ShouldBeEmpty_BeforeAnyLoad()
    {
        var definitions = new XapiDefinitions();
        definitions.LoadedApiNames.Should().BeEmpty();
    }

    [Fact]
    public void LoadDefaultXapis_ShouldLoadBothApis()
    {
        using var device = new BgapiDevice();
        device.LoadDefaultXapis();

        device.Definitions.HasDefinitions.Should().BeTrue();
        device.Definitions.LoadedApiNames.Should().Contain("bt");
        device.Definitions.LoadedApiNames.Should().Contain("btmesh");
        device.Definitions.LoadedApiNames.Should().HaveCount(2);
    }

    [Fact]
    public void LoadDefaultXapis_BtShouldHaveDeviceId4()
    {
        using var device = new BgapiDevice();
        device.LoadDefaultXapis();

        device.Definitions.GetDeviceId("bt").Should().Be(4);
    }

    [Fact]
    public void LoadDefaultXapis_BtMeshShouldHaveDeviceId5()
    {
        using var device = new BgapiDevice();
        device.LoadDefaultXapis();

        device.Definitions.GetDeviceId("btmesh").Should().Be(5);
    }

    [Fact]
    public void LoadDefaultXapis_ShouldResolveSystemHelloCommand()
    {
        using var device = new BgapiDevice();
        device.LoadDefaultXapis();

        var cmd = device.Definitions.GetCommand("bt", "system", "hello");
        cmd.Should().NotBeNull();
        cmd.Name.Should().Be("hello");
    }
}
