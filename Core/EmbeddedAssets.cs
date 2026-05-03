using System.IO.Compression;
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
        var manifestNames = assembly.GetManifestResourceNames();
        var manifestName = manifestNames
            .FirstOrDefault(name => string.Equals(name, resourceName, StringComparison.OrdinalIgnoreCase));

        if (manifestName is null)
        {
            var compressedManifestName = manifestNames
                .FirstOrDefault(name => string.Equals(name, resourceName + ".br", StringComparison.OrdinalIgnoreCase));
            if (compressedManifestName is not null)
            {
                using var compressedStream = assembly.GetManifestResourceStream(compressedManifestName);
                if (compressedStream is null)
                    throw new FileNotFoundException($"Embedded resource stream missing: {compressedManifestName}");

                Log.Information("Loaded compressed embedded asset '{FileName}' ({ResourceName}).", fileName, compressedManifestName);
                return ReadBrotliBytes(compressedStream);
            }

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

            var compressedFallbackPath = FindLooseAsset(fileName + ".br");
            if (compressedFallbackPath is not null)
            {
                Log.Warning(
                    "Embedded asset '{ResourceName}' was not found in {AssemblyLocation}; loading compressed loose file '{FallbackPath}' instead.",
                    resourceName,
                    assembly.Location,
                    compressedFallbackPath);
                using var compressedFile = File.OpenRead(compressedFallbackPath);
                return ReadBrotliBytes(compressedFile);
            }

            var matchingResources = string.Join(", ", manifestNames
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

    private static byte[] ReadBrotliBytes(Stream compressedStream)
    {
        using var brotli = new BrotliStream(compressedStream, CompressionMode.Decompress, leaveOpen: true);
        using var ms = new MemoryStream();
        brotli.CopyTo(ms);
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
