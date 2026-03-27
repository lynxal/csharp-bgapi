namespace CsharpBgapi;

/// <summary>
/// Configuration options for the CsharpBgapi library.
/// Bind from appsettings.json section "CsharpBgapi" or configure via <c>services.Configure&lt;CsharpBgapiOptions&gt;()</c>.
/// </summary>
public sealed class CsharpBgapiOptions
{
    /// <summary>Default baud rate for serial port communication.</summary>
    public int DefaultBaudRate { get; set; } = 115200;

    /// <summary>Serial port read timeout in milliseconds.</summary>
    public int SerialReadTimeoutMs { get; set; } = 1000;

    /// <summary>Serial port write timeout in milliseconds.</summary>
    public int SerialWriteTimeoutMs { get; set; } = 1000;

    /// <summary>Maximum retries for ReadExact when partial reads timeout.</summary>
    public int ReadExactMaxRetries { get; set; } = 5;

    /// <summary>Default timeout in seconds for waiting for command responses.</summary>
    public double ResponseTimeoutSeconds { get; set; } = 2.0;

    /// <summary>Read timeout in milliseconds for the background reader loop.</summary>
    public int ReaderLoopReadTimeoutMs { get; set; } = 100;

    /// <summary>Timeout in seconds for stopping the reader thread.</summary>
    public double StopReaderTimeoutSeconds { get; set; } = 2.0;

    /// <summary>Default max time in seconds for WaitEvents when no maxTime is specified.</summary>
    public double WaitEventsDefaultMaxTimeSeconds { get; set; } = 10.0;

    /// <summary>Maximum number of outer retries in RetryUntilAsync.</summary>
    public int RetryMax { get; set; } = 5;

    /// <summary>Interval in seconds between outer retries.</summary>
    public double RetryIntervalSeconds { get; set; } = 1.0;

    /// <summary>Maximum number of command-level retries for transient errors.</summary>
    public int RetryCmdMax { get; set; } = 6;

    /// <summary>Interval in seconds between command-level retries.</summary>
    public double RetryCmdIntervalSeconds { get; set; } = 1.0;
}
