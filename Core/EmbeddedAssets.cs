using System.Reflection;
using System.Linq;
using Serilog;

namespace UniversalUmap.Rendering.Core;

internal static class EmbeddedAssets
{
    private static string[] Tokenize(string value)
    {
        return value
            .Split(['/', '\\', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim())
            .Where(token => token.Length > 0)
            .ToArray();
    }

    private static bool ResourceMatches(string resourceName, string requestedName)
    {
        var resourceTokens = Tokenize(resourceName);
        var requestedTokens = Tokenize(requestedName);
        if (requestedTokens.Length == 0 || resourceTokens.Length < requestedTokens.Length)
            return false;

        var offset = resourceTokens.Length - requestedTokens.Length;
        for (var i = 0; i < requestedTokens.Length; i++)
        {
            if (!string.Equals(resourceTokens[offset + i], requestedTokens[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    public static byte[] ReadByFileName(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(x => ResourceMatches(x, fileName))
            .ToArray();

        if (resourceNames.Length == 0)
            throw new FileNotFoundException($"Embedded resource not found: {fileName}");

        // Prefer canonical asset paths (e.g. "...Assets.Shaders..."), then most-specific names.
        var resourceName = resourceNames
            .OrderByDescending(name => name.Contains(".Assets.", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(name => name.Length)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .First();

        if (resourceNames.Length > 1)
        {
            Log.Warning(
                "Multiple embedded resources matched '{FileName}'. Using '{ResourceName}'. Candidates: {Candidates}",
                fileName,
                resourceName,
                string.Join(", ", resourceNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new FileNotFoundException($"Embedded resource stream missing: {resourceName}");

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        Log.Information("Loaded embedded asset '{FileName}' ({ResourceName}).", fileName, resourceName);
        return ms.ToArray();
    }
}
