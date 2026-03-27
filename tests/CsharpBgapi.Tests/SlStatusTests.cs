using FluentAssertions;
using Xunit;

namespace CsharpBgapi.Tests;

public class SlStatusTests
{
    [Fact]
    public void OK_ShouldBeZero()
    {
        ((uint)SlStatus.OK).Should().Be(0x0000);
    }

    [Fact]
    public void Busy_ShouldBe0x0004()
    {
        ((uint)SlStatus.Busy).Should().Be(0x0004);
    }

    [Fact]
    public void BtMeshAlreadyExists_ShouldBe0x0501()
    {
        ((uint)SlStatus.BtMeshAlreadyExists).Should().Be(0x0501);
    }

    [Fact]
    public void UnknownValue_ShouldBe0xCAFEBABE()
    {
        ((uint)SlStatus.UnknownValue).Should().Be(0xCAFEBABE);
    }

    [Fact]
    public void FromUint_WithValidValue_ShouldReturnCorrectEnum()
    {
        SlStatusExtensions.FromUint(0x0004).Should().Be(SlStatus.Busy);
    }

    [Fact]
    public void FromUint_WithInvalidValue_ShouldReturnUnknownValue()
    {
        SlStatusExtensions.FromUint(0xFFFF).Should().Be(SlStatus.UnknownValue);
    }

    [Fact]
    public void GetName_ShouldReturnEnumName()
    {
        SlStatus.OK.GetName().Should().Be("OK");
        SlStatus.Timeout.GetName().Should().Be("Timeout");
    }
}
