using Microsoft.Extensions.DependencyInjection;

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
