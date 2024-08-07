using System.Collections.Generic;
using UniversalUmap.Rendering.Models;
using UniversalUmap.Rendering.Models.Materials;

namespace UniversalUmap.Rendering.Resources;

public static class ResourceCache
{
    public static Dictionary<string, Material> Materials { get; } = new();
    public static Dictionary<string, Texture> Textures { get; } = new();
}