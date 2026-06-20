using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Material.Parameters;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;

namespace UniversalUmap.Rendering;

public sealed record ExtractedTexture(
    string Path,
    bool SRGB,
    TextureCompressionSettings CompressionSettings,
    TextureAddress AddressX,
    TextureAddress AddressY);

public sealed record ComponentMask(bool R, bool G, bool B, bool A);

public sealed class ExtractedMaterial
{
    public string Type = "Material";
    public string? Parent;
    public string Path = string.Empty;
    public EMaterialShadingModel? ShadingModel;
    public EBlendMode? BlendMode;
    public bool TwoSided;
    public Dictionary<string, ComponentMask> ComponentSwitches = [];
    public Dictionary<string, ExtractedTexture> ReferenceTextures = [];
    public Dictionary<string, float> Scalars = [];
    public Dictionary<string, bool> Switches = [];
    public Dictionary<string, ExtractedTexture> Textures = [];
    public Dictionary<string, FLinearColor> Vectors = [];
}

public static class MaterialExtractor
{
    public static bool TryExtract(UObject material, out ExtractedMaterial result)
    {
        result = null!;
        try
        {
            result = Extract(material);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to extract material: {ex.Message}");
            return false;
        }
    }

    public static ExtractedMaterial Extract(UObject rootMaterial)
    {
        var result = new ExtractedMaterial
        {
            Path = rootMaterial.GetPathName(),
            Type = rootMaterial is UMaterial ? "Material" : "MaterialInstance"
        };

        UObject? currentMaterial = rootMaterial;

        while (currentMaterial != null)
        {
            if (currentMaterial is UMaterialInstanceConstant mic)
            {
                result.Parent = currentMaterial.GetPathName();

                if (mic.TryGetValue(out FStructFallback overrides, "BasePropertyOverrides"))
                {
                    if (overrides.TryGetValue(out EBlendMode blendMode, "BlendMode"))
                        result.BlendMode ??= blendMode;
                    if (overrides.TryGetValue(out EMaterialShadingModel shadingModel, "ShadingModel"))
                        result.ShadingModel ??= shadingModel;
                    if (overrides.TryGetValue(out bool twoSided, "TwoSided"))
                        result.TwoSided |= twoSided;
                }

                foreach (var tv in mic.TextureParameterValues ?? [])
                {
                    if (!tv.ParameterValue.TryLoad(out UTexture export))
                        continue;

                    try
                    {
                        result.Textures.TryAdd(tv.Name, ToExtractedTexture(export));
                    }
                    catch
                    {
                    }
                }

                foreach (var vp in mic.VectorParameterValues ?? [])
                    result.Vectors.TryAdd(vp.Name, vp.ParameterValue.GetValueOrDefault());
                foreach (var sp in mic.ScalarParameterValues ?? [])
                    result.Scalars.TryAdd(sp.Name, sp.ParameterValue);

                if (mic.StaticParameters != null)
                {
                    foreach (var sw in mic.StaticParameters.StaticSwitchParameters ?? [])
                        result.Switches.TryAdd(sw.ParameterInfo?.Name.Text ?? "None", sw.Value);
                    foreach (var cm in mic.StaticParameters.StaticComponentMaskParameters ?? [])
                        result.ComponentSwitches.TryAdd(cm.ParameterInfo?.Name.Text ?? "None", ToComponentMask(cm));
                }

                currentMaterial = mic.Parent;
            }
            else if (currentMaterial is UMaterialInstance materialInstance)
            {
                result.Parent = currentMaterial.GetPathName();

                if (materialInstance.TryGetValue(out FStructFallback overrides, "BasePropertyOverrides"))
                {
                    if (overrides.TryGetValue(out EBlendMode bm, "BlendMode"))
                        result.BlendMode ??= bm;
                    if (overrides.TryGetValue(out EMaterialShadingModel sm, "ShadingModel"))
                        result.ShadingModel ??= sm;
                    if (overrides.TryGetValue(out bool ts, "TwoSided"))
                        result.TwoSided |= ts;
                }

                if (materialInstance.StaticParameters != null)
                {
                    foreach (var sw in materialInstance.StaticParameters.StaticSwitchParameters ?? [])
                        if (sw.ParameterInfo?.Name.Text is { } nameText)
                            result.Switches.TryAdd(nameText, sw.Value);

                    foreach (var cm in materialInstance.StaticParameters.StaticComponentMaskParameters ?? [])
                        if (cm?.ParameterInfo?.Name.Text is { } nameText)
                            result.ComponentSwitches.TryAdd(nameText, ToComponentMask(cm));
                }
                currentMaterial = materialInstance.Parent;
            }
            else if (currentMaterial is UMaterial mat)
            {
                result.Parent = currentMaterial.GetPathName();

                result.ShadingModel ??= mat.GetOrDefault("ShadingModel", EMaterialShadingModel.MSM_DefaultLit);
                result.BlendMode ??= mat.GetOrDefault("BlendMode", EBlendMode.BLEND_Opaque);
                result.TwoSided |= mat.TwoSided;

                if (mat.CachedExpressionData!.TryGetValue(out FStructFallback cached, "Parameters") && cached.TryGetAllValues(out FStructFallback[] entries, "RuntimeEntries"))
                {
                    if (cached.TryGetValue(out float[] scalarVals, "ScalarValues") && entries.ElementAtOrDefault(0)?.TryGetValue(out FMaterialParameterInfo[] scalarInfos, "ParameterInfos") == true)
                        for (var i = 0; i < scalarInfos.Length && i < scalarVals.Length; i++)
                            result.Scalars.TryAdd(scalarInfos[i].Name.Text, scalarVals[i]);

                    if (cached.TryGetValue(out FLinearColor[] vecVals, "VectorValues") && entries.ElementAtOrDefault(1)?.TryGetValue(out FMaterialParameterInfo[] vecInfos, "ParameterInfos") == true)
                        for (var i = 0; i < vecInfos.Length && i < vecVals.Length; i++)
                            result.Vectors.TryAdd(vecInfos[i].Name.Text, vecVals[i]);

                    if (cached.TryGetValue(out FPackageIndex[] texVals, "TextureValues") && entries.ElementAtOrDefault(2)?.TryGetValue(out FMaterialParameterInfo[] texInfos, "ParameterInfos") == true)
                        for (var i = 0; i < texInfos.Length && i < texVals.Length; i++)
                        {
                            if (!texVals[i].TryLoad(out UTexture export))
                                continue;

                            try
                            {
                                result.Textures.TryAdd(texVals[i].Name, ToExtractedTexture(export));
                            }
                            catch
                            {
                            }
                        }
                }

                foreach (var texture in mat.ReferencedTextures)
                {
                    if (texture is null)
                        continue;

                    try
                    {
                        result.ReferenceTextures.TryAdd("REF_" + texture.Name, ToExtractedTexture(texture));
                    }
                    catch
                    {
                    }
                }

                for (var i = 0; i < mat.Expressions.Length; i++)
                {
                    if (mat.Expressions[i] == null || !mat.Expressions[i].TryLoad(out var expr))
                        continue;

                    switch (expr)
                    {
                        case UMaterialExpressionTextureSampleParameter tsp:
                            if (tsp.Texture is null)
                                break;

                            try
                            {
                                var key = tsp.ParameterName.Text.Equals("None") ? $@"Texture_{i}" : tsp.ParameterName.Text;
                                result.Textures.TryAdd(key, ToExtractedTexture(tsp.Texture));
                            }
                            catch
                            {
                            }
                            break;
                        case UMaterialExpressionVectorParameter vp:
                            result.Vectors.TryAdd(vp.ParameterName.Text.Equals("None") ? $"Vector_{i}" : vp.ParameterName.Text, vp.DefaultValue);
                            break;
                        case UMaterialExpressionScalarParameter sp:
                            result.Scalars.TryAdd(sp.ParameterName.Text.Equals("None") ? $"Scalar_{i}" : sp.ParameterName.Text, sp.DefaultValue);
                            break;
                        case UMaterialExpressionStaticBoolParameter bp:
                            result.Switches.TryAdd(bp.ParameterName.Text.Equals("None") ? $"Switch_{i}" : bp.ParameterName.Text, bp.DefaultValue);
                            break;
                    }
                }
                break;
            }
            else
                throw new NotImplementedException("Material of Type " + currentMaterial.GetType().FullName + " is not supported");
        }

        result.ShadingModel ??= EMaterialShadingModel.MSM_DefaultLit;
        result.BlendMode ??= EBlendMode.BLEND_Opaque;

        return result;
    }

    private static ExtractedTexture ToExtractedTexture(UTexture texture)
    {
        return new ExtractedTexture(
            texture.GetPathName(),
            texture.SRGB,
            texture.CompressionSettings,
            texture.GetTextureAddressX(),
            texture.GetTextureAddressY());
    }

    private static ComponentMask ToComponentMask(FStaticComponentMaskParameter mask)
    {
        return new ComponentMask(mask.R, mask.G, mask.B, mask.A);
    }
}
