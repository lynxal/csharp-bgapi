using CsharpBgapi.Commands;
using FluentAssertions;
using Xunit;

namespace CsharpBgapi.Tests;

// Build used to leave the builder's api, class, command and parameters in place, so an instance
// pointed at a second command silently reused any parameter of the same name set for the first.
// Build now consumes that state, which also means a second Build hits the "must be set before
// building" guard instead of encoding a frame with every parameter defaulted to zero.
public class CommandBuilderTests
{
    // bt.system.halt: device id 4, class index 1, command index 12, one uint8 param named "halt".
    private static readonly byte[] HaltOne = [0x20, 0x01, 0x01, 0x0C, 0x01];
    private static readonly byte[] HaltDefaulted = [0x20, 0x01, 0x01, 0x0C, 0x00];

    [Fact]
    public void Build_ShouldEncodeConfiguredCommand()
    {
        using var device = new BgapiDevice();
        device.LoadDefaultXapis();
        var builder = new CommandBuilder(device.Protocol);

        var frame = builder.Api("bt").Class("system").Command("halt").Param("halt", (byte)1).Build();

        frame.Should().Equal(HaltOne);
    }

    [Fact]
    public void Build_SecondCallOnSameInstance_ShouldThrow()
    {
        using var device = new BgapiDevice();
        device.LoadDefaultXapis();
        var builder = new CommandBuilder(device.Protocol);
        builder.Api("bt").Class("system").Command("halt").Param("halt", (byte)1).Build();

        builder.Invoking(b => b.Build())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*must be set before building*");
    }

    [Fact]
    public void Build_ShouldNotCarryParametersIntoTheNextCommand()
    {
        using var device = new BgapiDevice();
        device.LoadDefaultXapis();
        var builder = new CommandBuilder(device.Protocol);
        builder.Api("bt").Class("system").Command("halt").Param("halt", (byte)1).Build();

        var second = builder.Api("bt").Class("system").Command("halt").Build();

        second.Should().Equal(HaltDefaulted);
    }
}
