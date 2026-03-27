using Microsoft.Extensions.DependencyInjection;

namespace SilabsBgapi;

public static class SilabsBgapiServiceExtensions
{
    /// <summary>
    /// Registers SilabsBgapi services in the DI container.
    /// Optionally configure <see cref="SilabsBgapiOptions"/> inline.
    /// For config-file binding, use <c>services.Configure&lt;SilabsBgapiOptions&gt;(configuration.GetSection("SilabsBgapi"))</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options configuration delegate.</param>
    /// <param name="loadDefaultXapis">
    /// If true, the built-in XAPI definitions (Bluetooth and Bluetooth Mesh)
    /// are loaded after <see cref="BgapiDevice"/> construction. Default is false.
    /// </param>
    public static IServiceCollection AddSilabsBgapi(
        this IServiceCollection services,
        Action<SilabsBgapiOptions>? configure = null,
        bool loadDefaultXapis = false)
    {
        var builder = services.AddOptions<SilabsBgapiOptions>();
        if (configure is not null) builder.Configure(configure);

        if (loadDefaultXapis)
        {
            services.AddSingleton<BgapiDevice>(sp =>
            {
                var device = ActivatorUtilities.CreateInstance<BgapiDevice>(sp);
                device.LoadDefaultXapis();
                return device;
            });
        }
        else
        {
            services.AddSingleton<BgapiDevice>();
        }

        return services;
    }
}
