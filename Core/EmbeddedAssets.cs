using System.Reflection;
using Serilog;

namespace UniversalUmap.Rendering.Core;

internal static class EmbeddedAssets
{
    public static byte[] ReadByFileName(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            throw new FileNotFoundException($"Embedded resource not found: {fileName}");

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new FileNotFoundException($"Embedded resource stream missing: {resourceName}");

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        Log.Information("Loaded embedded asset '{FileName}' ({ResourceName}).", fileName, resourceName);
        return ms.ToArray();
    }
}
