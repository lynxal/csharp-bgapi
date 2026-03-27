using System.Reflection;

namespace CsharpBgapi.Protocol;

/// <summary>
/// Provides access to XAPI definition files embedded as assembly resources.
/// </summary>
internal static class EmbeddedXapiResources
{
    private static readonly Assembly ResourceAssembly = typeof(EmbeddedXapiResources).Assembly;

    /// <summary>
    /// All embedded XAPI resource names, in load order.
    /// </summary>
    internal static IReadOnlyList<string> AllResourceNames { get; } =
    [
        "CsharpBgapi.Xapi.sl_bt.xapi",
        "CsharpBgapi.Xapi.sl_btmesh.xapi",
    ];

    /// <summary>
    /// Opens an embedded XAPI resource stream. Throws if the resource is not found.
    /// </summary>
    internal static Stream OpenResource(string resourceName)
    {
        return ResourceAssembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded XAPI resource '{resourceName}' not found in assembly '{ResourceAssembly.FullName}'.");
    }
}
