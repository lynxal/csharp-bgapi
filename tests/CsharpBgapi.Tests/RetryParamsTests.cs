using CsharpBgapi.Events;
using FluentAssertions;
using Xunit;

namespace CsharpBgapi.Tests;

// RetryParams' own literals are the fallback for every field a hand-built instance leaves alone,
// so they must equal the CsharpBgapiOptions defaults. RetryCmdMax used to read 10 against the
// option's 6: a caller building a RetryParams to set one field silently ran six-to-ten command
// retries nobody configured. Compared against FromOptions rather than literal by literal, so a
// field added later is pinned too.
public class RetryParamsTests
{
    [Fact]
    public void Defaults_ShouldMatchOptionDefaults()
    {
        var defaults = new RetryParams();

        var fromOptions = RetryParams.FromOptions(new CsharpBgapiOptions());

        defaults.Should().Be(fromOptions);
    }

    [Fact]
    public void FromOptions_ShouldCarryConfiguredValues()
    {
        var options = new CsharpBgapiOptions
        {
            RetryMax = 2,
            RetryIntervalSeconds = 3.0,
            RetryCmdMax = 4,
            RetryCmdIntervalSeconds = 5.0,
        };

        var retryParams = RetryParams.FromOptions(options);

        retryParams.RetryMax.Should().Be(2);
        retryParams.RetryInterval.Should().Be(TimeSpan.FromSeconds(3));
        retryParams.RetryCmdMax.Should().Be(4);
        retryParams.RetryCmdInterval.Should().Be(TimeSpan.FromSeconds(5));
    }
}
