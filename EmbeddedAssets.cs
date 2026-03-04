using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Serilog;

namespace UniversalUmap.Rendering;

internal static class EmbeddedAssets
{
    public static bool TryReadByFileName(string fileName, out byte[] bytes)
    {
        try
        {
            bytes = ReadByFileName(fileName);
            return true;
        }
        catch
        {
            bytes = [];
            return false;
        }
    }

    public static byte[] ReadByFileName(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            if (TryReadShaderFromDisk(fileName, out var diskBytes))
            {
                Log.Information("Loaded shader asset '{FileName}' from disk fallback.", fileName);
                return diskBytes;
            }

            throw new FileNotFoundException($"Embedded resource not found: {fileName}");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new FileNotFoundException($"Embedded resource stream missing: {resourceName}");

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        Log.Information("Loaded embedded asset '{FileName}' ({ResourceName}).", fileName, resourceName);
        return ms.ToArray();
    }

    private static bool TryReadShaderFromDisk(string fileName, out byte[] bytes)
    {
        static string? Probe(string root, string shaderFile)
        {
            if (string.IsNullOrWhiteSpace(root))
                return null;

            var direct = Path.Combine(root, "Assets", "Shaders", shaderFile);
            if (File.Exists(direct))
                return direct;

            var tonemapping = Path.Combine(root, "Assets", "Shaders", "Tonemapping", shaderFile);
            if (File.Exists(tonemapping))
                return tonemapping;

            var widgets = Path.Combine(root, "Assets", "Shaders", "Widgets", shaderFile);
            if (File.Exists(widgets))
                return widgets;

            return null;
        }

        var baseDir = AppContext.BaseDirectory;
        var cwd = Environment.CurrentDirectory;

        var path = Probe(baseDir, fileName)
                   ?? Probe(cwd, fileName)
                   ?? Probe(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..")), fileName)
                   ?? Probe(Path.GetFullPath(Path.Combine(cwd, "UniversalUmap.Rendering")), fileName);

        if (path is null)
        {
            bytes = [];
            return false;
        }

        bytes = File.ReadAllBytes(path);
        return true;
    }
}
