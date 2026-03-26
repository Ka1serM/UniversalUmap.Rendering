using System.Reflection;
using System.Linq;
using Serilog;

namespace UniversalUmap.Rendering.Core;

internal static class EmbeddedAssets
{
    private static string ToResourceName(string path)
    {
        return path
            .Trim()
            .Replace('\\', '.')
            .Replace('/', '.');
    }

    public static byte[] ReadByFileName(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = ToResourceName(fileName);
        var manifestName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => string.Equals(name, resourceName, StringComparison.OrdinalIgnoreCase));

        if (manifestName is null)
            throw new FileNotFoundException($"Embedded resource not found: {resourceName}");

        using var stream = assembly.GetManifestResourceStream(manifestName);
        if (stream is null)
            throw new FileNotFoundException($"Embedded resource stream missing: {manifestName}");

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        Log.Information("Loaded embedded asset '{FileName}' ({ResourceName}).", fileName, manifestName);
        return ms.ToArray();
    }
}
