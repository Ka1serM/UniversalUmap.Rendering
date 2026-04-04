using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using UniversalUmap.Rendering.Models;

namespace UniversalUmap.Rendering;

/// <summary>
/// Resolves texture paths from materials using AutoTexture rules.
/// </summary>
public static class TextureResolver
{
    /// <summary>
    /// Resolves a texture path for a given material slot using AutoTexture rules.
    /// </summary>
    /// <param name="material">The material to resolve textures from.</param>
    /// <param name="autoTextureParameter">The parameter name (e.g., "Color", "Normal").</param>
    /// <param name="autoTextureEnabled">Whether AutoTexture matching is enabled.</param>
    /// <param name="autoTextureItems">The AutoTexture rules to apply.</param>
    /// <param name="fallbackKeys">Fallback texture keys to try if AutoTexture doesn't match.</param>
    /// <returns>The resolved texture path, or null if not found.</returns>
    public static string? ResolveTexturePath(
        UObject material,
        string autoTextureParameter,
        bool autoTextureEnabled,
        IReadOnlyList<AutoTextureItem> autoTextureItems,
        params string[] fallbackKeys)
    {
        if (autoTextureEnabled && autoTextureItems.Count > 0)
        {
            var rule = autoTextureItems
                .FirstOrDefault(item => string.Equals(item.Parameter, autoTextureParameter, StringComparison.OrdinalIgnoreCase));
            if (rule is not null)
            {
                var fromRule = FindTexturePathByAutoTextureRule(material, rule);
                if (!string.IsNullOrWhiteSpace(fromRule))
                    return fromRule;
            }
        }

        return FindTexturePath(material, fallbackKeys);
    }

    /// <summary>
    /// Builds a complete material data structure by resolving all texture slots.
    /// </summary>
    /// <param name="material">The material to process.</param>
    /// <param name="autoTextureEnabled">Whether AutoTexture matching is enabled.</param>
    /// <param name="autoTextureItems">The AutoTexture rules to apply.</param>
    /// <param name="textureLoader">Function to load a texture path and return its index (-1 if failed).</param>
    /// <returns>The populated MaterialData structure.</returns>
    public static MaterialData BuildMaterialData(
        UObject material,
        bool autoTextureEnabled,
        IReadOnlyList<AutoTextureItem> autoTextureItems,
        Func<string?, int> textureLoader)
    {
        var data = new MaterialData();

        var albedo = ResolveTexturePath(material, "Color", autoTextureEnabled, autoTextureItems, "BaseColor", "Albedo", "Diffuse");
        var normal = ResolveTexturePath(material, "Normal", autoTextureEnabled, autoTextureItems, "Normal");
        var roughness = ResolveTexturePath(material, "Roughness", autoTextureEnabled, autoTextureItems, "Roughness");
        var metallic = ResolveTexturePath(material, "Metallic", autoTextureEnabled, autoTextureItems, "Metallic", "Metalness");
        var specular = ResolveTexturePath(material, "Specular", autoTextureEnabled, autoTextureItems, "Specular");
        var emission = ResolveTexturePath(material, "Emissive", autoTextureEnabled, autoTextureItems, "Emissive", "Emission");
        var opacity = ResolveTexturePath(material, "Alpha", autoTextureEnabled, autoTextureItems, "Opacity", "Mask");

        data.AlbedoIndex = textureLoader(albedo);
        data.NormalIndex = textureLoader(normal);
        data.RoughnessIndex = textureLoader(roughness);
        data.MetallicIndex = textureLoader(metallic);
        data.SpecularIndex = textureLoader(specular);
        data.EmissionIndex = textureLoader(emission);
        data.OpacityIndex = textureLoader(opacity);

        return data;
    }

    /// <summary>
    /// Builds material data for a UStaticMesh using AutoTexture rules.
    /// </summary>
    /// <param name="staticMesh">The static mesh containing material references.</param>
    /// <param name="autoTextureEnabled">Whether AutoTexture matching is enabled.</param>
    /// <param name="autoTextureItems">The AutoTexture rules to apply.</param>
    /// <param name="textureLoader">Function to load a texture path and return its index (-1 if failed).</param>
    /// <returns>Array of MaterialData, one per material slot.</returns>
    public static MaterialData[] BuildMaterialDataForStaticMesh(
        CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh staticMesh,
        bool autoTextureEnabled,
        IReadOnlyList<AutoTextureItem> autoTextureItems,
        Func<string?, int> textureLoader)
    {
        var materialCount = Math.Max(1, staticMesh.Materials?.Length ?? 0);
        var materials = new MaterialData[materialCount];

        for (var i = 0; i < materialCount; i++)
        {
            materials[i] = new MaterialData();

            if (staticMesh.Materials is null || i >= staticMesh.Materials.Length)
                continue;

            var materialRef = staticMesh.Materials[i];
            var material = materialRef?.Load<UMaterialInterface>();
            if (material is null)
                continue;

            materials[i] = BuildMaterialData(material, autoTextureEnabled, autoTextureItems, textureLoader);
        }

        return materials;
    }

    /// <summary>
    /// Builds material data for a landscape using AutoTexture rules.
    /// </summary>
    /// <param name="landscapeMaterial">The landscape material interface.</param>
    /// <param name="autoTextureEnabled">Whether AutoTexture matching is enabled.</param>
    /// <param name="autoTextureItems">The AutoTexture rules to apply.</param>
    /// <param name="textureLoader">Function to load a texture path and return its index (-1 if failed).</param>
    /// <returns>Array of MaterialData (single element for landscape).</returns>
    public static MaterialData[] BuildMaterialDataForLandscape(
        UMaterialInterface? landscapeMaterial,
        bool autoTextureEnabled,
        IReadOnlyList<AutoTextureItem> autoTextureItems,
        Func<string?, int> textureLoader)
    {
        if (landscapeMaterial is null)
            return [new MaterialData()];

        try
        {
            return [BuildMaterialData(landscapeMaterial, autoTextureEnabled, autoTextureItems, textureLoader)];
        }
        catch
        {
            return [new MaterialData()];
        }
    }

    private static string? FindTexturePathByAutoTextureRule(UObject material, AutoTextureItem rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name))
            return null;

        var textures = ExtractTexturesFromMaterial(material);
        foreach (var pair in textures)
        {
            var key = pair.Key ?? string.Empty;
            var path = pair.Value?.GetPathName() ?? string.Empty;

            if (IsBlacklisted(rule, key) || IsBlacklisted(rule, path))
                continue;

            if ((RegexIsMatchSafe(key, rule.Name) || RegexIsMatchSafe(path, rule.Name)) && !string.IsNullOrWhiteSpace(path))
                return path;
        }

        return null;
    }

    private static string? FindTexturePath(UObject material, params string[] keys)
    {
        var textures = ExtractTexturesFromMaterial(material);
        foreach (var key in keys)
        {
            var match = textures.FirstOrDefault(x => x.Key.Contains(key, StringComparison.OrdinalIgnoreCase)).Value;
            if (match is not null && !string.IsNullOrWhiteSpace(match.GetPathName()))
                return match.GetPathName();
        }

        return null;
    }

    private static Dictionary<string, UTexture> ExtractTexturesFromMaterial(UObject material)
    {
        var textures = new Dictionary<string, UTexture>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (material is UMaterialInstanceConstant mic)
            {
                foreach (var tv in mic.TextureParameterValues ?? [])
                {
                    if (tv.ParameterValue.TryLoad(out UTexture export))
                        textures.TryAdd(tv.Name, export);
                }
            }
            else if (material is UMaterialInstance materialInstance)
            {
                // Handle other material instance types if needed
            }
            else if (material is UMaterial mat)
            {
                // Handle base materials if needed
                if (mat.CachedExpressionData?.TryGetValue(out FStructFallback cached, "Parameters") == true &&
                    cached.TryGetValue(out FPackageIndex[] texVals, "TextureValues") &&
                    cached.TryGetAllValues(out FStructFallback[] entries, "RuntimeEntries") &&
                    entries?.ElementAtOrDefault(0)?.TryGetValue(out FMaterialParameterInfo[] texInfos, "ParameterInfos") == true)
                {
                    for (var i = 0; i < texInfos?.Length && i < texVals?.Length; i++)
                    {
                        if (texVals[i] is not null && texVals[i].TryLoad(out UTexture export))
                            textures.TryAdd(texInfos[i].Name.Text, export);
                    }
                }
            }
        }
        catch
        {
            // Ignore extraction errors
        }

        return textures;
    }

    private static bool IsBlacklisted(AutoTextureItem rule, string input)
    {
        if (string.IsNullOrWhiteSpace(rule.Blacklist) || string.IsNullOrWhiteSpace(input))
            return false;
        return RegexIsMatchSafe(input, rule.Blacklist);
    }

    private static bool RegexIsMatchSafe(string input, string pattern)
    {
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(pattern))
            return false;

        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
