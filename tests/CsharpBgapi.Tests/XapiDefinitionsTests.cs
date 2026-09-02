using System.Text;
using FluentAssertions;
using CsharpBgapi.Protocol;
using Xunit;

namespace CsharpBgapi.Tests;

public class XapiDefinitionsTests
{
    private const string ValidXapi =
        """
        <api device_id="9" device_name="test">
          <class index="1" name="system">
            <command index="0" name="hello">
              <returns><param name="result" type="errorcode"/></returns>
            </command>
          </class>
        </api>
        """;

    private static Stream StreamOf(string xml) => new MemoryStream(Encoding.UTF8.GetBytes(xml));

    [Fact]
    public void LoadFromStream_ShouldReturnParsedApi()
    {
        var definitions = new XapiDefinitions();

        var api = definitions.LoadFromStream(StreamOf(ValidXapi));

        api.Name.Should().Be("test");
        api.DeviceId.Should().Be(9);
        api.Classes.Should().ContainSingle(c => c.Name == "system");
    }

    [Fact]
    public void LoadFromStream_ShouldThrow_WhenDeviceIdMissing()
    {
        var definitions = new XapiDefinitions();
        var xml = """<api device_name="test"><class index="1" name="system"/></api>""";

        definitions.Invoking(d => d.LoadFromStream(StreamOf(xml)))
            .Should().Throw<InvalidOperationException>().WithMessage("*device_id*");
    }

    [Fact]
    public void LoadFromStream_ShouldThrow_WhenDeviceNameMissing()
    {
        var definitions = new XapiDefinitions();
        var xml = """<api device_id="9"><class index="1" name="system"/></api>""";

        definitions.Invoking(d => d.LoadFromStream(StreamOf(xml)))
            .Should().Throw<InvalidOperationException>().WithMessage("*device_name*");
    }

    [Fact]
    public void LoadFromStream_ShouldThrow_WhenNoClasses()
    {
        var definitions = new XapiDefinitions();
        var xml = """<api device_id="9" device_name="test"></api>""";

        definitions.Invoking(d => d.LoadFromStream(StreamOf(xml)))
            .Should().Throw<InvalidOperationException>().WithMessage("*no classes*");
    }

    // A second API claiming a device id already loaded used to be accepted: Reindex assigns into
    // _commandsByHeader/_eventsByHeader by plain indexer, so the newcomer silently overwrote the
    // first API's entries for every colliding (class, index) pair. Frames then decoded under the
    // wrong definition, and MaxPayloadFor -- read by IsKnownHeader and the resync plausibility
    // check -- answered from the wrong definition too.
    [Fact]
    public void LoadFromStream_ShouldThrow_WhenDeviceIdAlreadyLoadedUnderAnotherName()
    {
        var definitions = new XapiDefinitions();
        var other = """
            <api device_id="9" device_name="clash">
              <class index="1" name="system">
                <command index="0" name="hello">
                  <returns><param name="result" type="errorcode"/></returns>
                </command>
              </class>
            </api>
            """;
        definitions.LoadFromStream(StreamOf(ValidXapi));

        definitions.Invoking(d => d.LoadFromStream(StreamOf(other)))
            .Should().Throw<InvalidOperationException>().WithMessage("*device_id 9*already loaded*test*");

        definitions.LoadedApiNames.Should().BeEquivalentTo(["test"]);
    }

    [Fact]
    public void LoadFromStream_ShouldReplaceItself_WhenSameApiReloaded()
    {
        var definitions = new XapiDefinitions();
        definitions.LoadFromStream(StreamOf(ValidXapi));

        definitions.Invoking(d => d.LoadFromStream(StreamOf(ValidXapi))).Should().NotThrow();

        definitions.LoadedApiNames.Should().BeEquivalentTo(["test"]);
    }
}
