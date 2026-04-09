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
        {
            var fallbackPath = FindLooseAsset(fileName);
            if (fallbackPath is not null)
            {
                Log.Warning(
                    "Embedded asset '{ResourceName}' was not found in {AssemblyLocation}; loading loose file '{FallbackPath}' instead.",
                    resourceName,
                    assembly.Location,
                    fallbackPath);
                return File.ReadAllBytes(fallbackPath);
            }

            var matchingResources = string.Join(", ", assembly.GetManifestResourceNames()
                .Where(name => name.Contains(Path.GetFileName(fileName), StringComparison.OrdinalIgnoreCase))
                .Take(8));
            throw new FileNotFoundException(
                $"Embedded resource not found: {resourceName}. Assembly: {assembly.Location}. Similar resources: {matchingResources}");
        }

        using var stream = assembly.GetManifestResourceStream(manifestName);
        if (stream is null)
            throw new FileNotFoundException($"Embedded resource stream missing: {manifestName}");

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        Log.Information("Loaded embedded asset '{FileName}' ({ResourceName}).", fileName, manifestName);
        return ms.ToArray();
    }

    private static string? FindLooseAsset(string fileName)
    {
        var normalized = fileName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, normalized),
            Path.Combine(Environment.CurrentDirectory, normalized),
            Path.Combine(Environment.CurrentDirectory, "..", "UniversalUmap.Rendering", normalized),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "UniversalUmap.Rendering", normalized),
        };

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);
    }
}
