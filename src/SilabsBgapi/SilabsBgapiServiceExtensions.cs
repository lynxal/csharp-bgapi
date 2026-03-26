using Microsoft.Extensions.DependencyInjection;

namespace SilabsBgapi;

public static class SilabsBgapiServiceExtensions
{
    /// <summary>
    /// Registers SilabsBgapi services in the DI container.
    /// Optionally configure <see cref="SilabsBgapiOptions"/> inline.
    /// For config-file binding, use <c>services.Configure&lt;SilabsBgapiOptions&gt;(configuration.GetSection("SilabsBgapi"))</c>.
    /// </summary>
    public static IServiceCollection AddSilabsBgapi(
        this IServiceCollection services, Action<SilabsBgapiOptions>? configure = null)
    {
        var builder = services.AddOptions<SilabsBgapiOptions>();
        if (configure is not null) builder.Configure(configure);
        services.AddSingleton<BgapiDevice>();
        return services;
    }
}
