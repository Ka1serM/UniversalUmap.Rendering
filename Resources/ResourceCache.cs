using CUE4Parse.Utils;
using UniversalUmap.Rendering.Models;
using UniversalUmap.Rendering.Models.Materials;

namespace UniversalUmap.Rendering.Resources;

public static class ResourceCache
{
    private static readonly object Monitor = new();
    private static readonly Dictionary<string, Mesh> Meshes = new();
    private static readonly Dictionary<string, Material> Materials = new();
    private static readonly Dictionary<string, Texture> Textures = new();
    
    public static Mesh GetOrAdd(string key, Func<Mesh> valueFactory)
    {
        lock (Monitor)
            return Meshes.GetOrAdd(key, valueFactory);
    }
    
    public static Material GetOrAdd(string key, Func<Material> valueFactory)
    {
        lock (Monitor)
            return Materials.GetOrAdd(key, valueFactory);
    }

    public static Texture GetOrAdd(string key, Func<Texture> valueFactory)
    {
        lock (Monitor)
            return Textures.GetOrAdd(key, valueFactory);
    }

    public static void Clear()
    {
        lock (Monitor)
        {
            foreach (var mesh in Meshes)
                mesh.Value.Dispose();
            Meshes.Clear();
            Materials.Clear();
            foreach (var texture in Textures)
                texture.Value.Dispose();
            Textures.Clear();
        }
    }
}