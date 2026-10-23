using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CsharpBgapi;

public static class CsharpBgapiServiceExtensions
{
    /// <summary>
    /// Registers CsharpBgapi services in the DI container.
    /// Optionally configure <see cref="CsharpBgapiOptions"/> inline.
    /// For config-file binding, use <c>services.Configure&lt;CsharpBgapiOptions&gt;(configuration.GetSection("CsharpBgapi"))</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options configuration delegate.</param>
    /// <param name="loadDefaultXapis">
    /// If true, the built-in XAPI definitions (Bluetooth and Bluetooth Mesh)
    /// are loaded after <see cref="BgapiDevice"/> construction. Default is true.
    /// </param>
    public static IServiceCollection AddCsharpBgapi(
        this IServiceCollection services,
        Action<CsharpBgapiOptions>? configure = null,
        bool loadDefaultXapis = true)
    {
        var builder = services.AddOptions<CsharpBgapiOptions>();
        if (configure is not null) builder.Configure(configure);

        // The options constructor is called explicitly. Left to DI, a host with a plain ILogger and
        // no ILoggerFactory registered could satisfy both it and BgapiDevice(ILogger?), neither of
        // which takes the other's parameters, and resolving the device threw as ambiguous.
        services.AddSingleton(sp =>
        {
            var device = new BgapiDevice(
                sp.GetRequiredService<IOptions<CsharpBgapiOptions>>(),
                sp.GetService<ILoggerFactory>());
            if (loadDefaultXapis)
                device.LoadDefaultXapis();
            return device;
        });

        return services;
    }
}
