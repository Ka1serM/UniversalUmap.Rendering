using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;

namespace UniversalUmap.Rendering.Models.Materials;

public record MaterialParameters
{
    public bool TwoSided;
    public string BlendMode;
    public string ShadingModel;
    public readonly Dictionary<string, UTexture> Textures = new();
    public readonly Dictionary<string, UTexture> ReferenceTextures = new();
}

public static class MaterialProcessor
{
    public static MaterialParameters GetMaterialParameters(this UObject mat)
    {
        var parameters = new MaterialParameters();
        UObject obj = mat;
        while (obj != null)
        {
            if (obj is UMaterialInstanceConstant parentInstance)
            {
                ReadUMaterialInstanceParams(parentInstance, parameters);
                obj = parentInstance.Parent;
            }
            else if (obj is UMaterial parentMaterial)
            {
                ReadUMaterialParams(parentMaterial, parameters);
                break;
            }
            else
                break;
        }
        return parameters;
    }
    

    private static void AddOrSkipProperty<TValue>(Dictionary<string, TValue> dictionary, string key, TValue value)
    {
        if (key == null || value == null || dictionary.ContainsKey(key))
            return;
        dictionary.Add(key, value);
    }

    private static void WriteParameters<T>(Dictionary<string, T> dict, FPropertyTag runtimeEntryTag, T[] parameterValues)
    {
        var runtimeEntrySet = runtimeEntryTag.Tag?.GetValue(typeof(FStructFallback)) as FStructFallback;
        var parameterInfosTag = runtimeEntrySet?.Properties.FirstOrDefault(property => property.Name.Text == "ParameterInfos");
        var parameterInfos = parameterInfosTag?.Tag?.GetValue(typeof(FMaterialParameterInfo[])) as FMaterialParameterInfo[];
        if (parameterInfos == null) return;
        for (var i = 0; i < parameterInfos.Length && i < parameterValues.Length; i++)
            AddOrSkipProperty(dict, parameterInfos[i].Name.Text, parameterValues[i]);
    }
    
    private static void ReadUMaterialParams(UMaterial material, MaterialParameters parameters)
    {
        var cachedExpressionDataTag = material.Properties.FirstOrDefault(property => property.Name.Text == "CachedExpressionData");
        var cachedExpressionData = cachedExpressionDataTag?.Tag?.GetValue(typeof(FStructFallback)) as FStructFallback;
        var parametersTag = cachedExpressionData?.Properties.FirstOrDefault(property => property.Name.Text == "Parameters");
        var cachedParameters = parametersTag?.Tag?.GetValue(typeof(FStructFallback)) as FStructFallback;
        var textureValuesTag = cachedParameters?.Properties.FirstOrDefault(property => property.Name.Text == "TextureValues");
        var textureRefs = textureValuesTag?.Tag?.GetValue(typeof(FPackageIndex[])) as FPackageIndex[];

        UTexture[] textureValues = null;
        if (textureRefs != null)
        {
            textureValues = new UTexture[textureRefs.Length];
            for (var i = 0; i < textureRefs.Length; i++)
                textureValues[i] = textureRefs[i].Load<UTexture>(); 
        }
        
        var runtimeEntriesTags = cachedParameters?.Properties.Where(property => property.Name.Text == "RuntimeEntries").ToArray();
        if (runtimeEntriesTags != null)
            if (runtimeEntriesTags.Length > 2 && textureValues != null)
                WriteParameters(parameters.Textures, runtimeEntriesTags[2], textureValues);

        //Ref Textures
        foreach (var texture in material.ReferencedTextures)
            if(texture != null) //this can be null!!
                AddOrSkipProperty(parameters.ReferenceTextures, texture.Name, texture);
        
        //Expressions
        for (var i = 0; i < material.Expressions.Length; i++)
        {
            if (!material.Expressions[i].TryLoad(out var expression))
                continue;
            switch (expression)
            {
                case UMaterialExpressionTextureSampleParameter textureSampleParam:
                    if (textureSampleParam.Texture == null)
                        continue;
                    AddOrSkipProperty(parameters.Textures,
                        textureSampleParam.ParameterName == "None" ? "Texture" + i : textureSampleParam.ParameterName.Text,
                        textureSampleParam.Texture);
                    break;
                case UMaterialExpressionTextureSample textureSample:
                    if (textureSample.Texture == null)
                        continue;
                    AddOrSkipProperty(parameters.ReferenceTextures, textureSample.Texture.Name, textureSample.Texture);
                    break;
            }
        }
    }

    private static void ReadUMaterialInstanceParams(UMaterialInstanceConstant material, MaterialParameters parameters)
    {
        var basePropertyOverridesTag = material.Properties.FirstOrDefault(property => property.Name.Text == "BasePropertyOverrides");
        var basePropertyOverrides = basePropertyOverridesTag?.Tag?.GetValue(typeof(FStructFallback)) as FStructFallback;

        var blendModeTag = basePropertyOverrides?.Properties.FirstOrDefault(property => property.Name.Text == "BlendMode");
        parameters.BlendMode = blendModeTag?.Tag?.GenericValue?.ToString();

        var shadingModelTag = basePropertyOverrides?.Properties.FirstOrDefault(property => property.Name.Text == "ShadingModel");
        parameters.ShadingModel = shadingModelTag?.Tag?.GenericValue?.ToString();

        var twoSidedTag = basePropertyOverrides?.Properties.FirstOrDefault(property => property.Name.Text == "TwoSided");
        if (twoSidedTag?.Tag?.GenericValue != null)
            parameters.TwoSided = (bool)twoSidedTag.Tag.GenericValue;

        foreach (var textureParam in material.TextureParameterValues)
        {
            if (!textureParam.ParameterValue.TryLoad(out UTexture texture))
                continue;
            AddOrSkipProperty(parameters.Textures, textureParam.Name, texture);
        }
    }
}