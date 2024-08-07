using System;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.Utils;
using UniversalUmap.Rendering.Resources;
using Veldrid;

namespace UniversalUmap.Rendering.Models.Materials;

public class Material : IDisposable
{
    private bool IsDisposed;
    
    private readonly GraphicsDevice GraphicsDevice;
    private readonly CommandList CommandList;
    
    private Texture Color;
    private Texture Metallic;
    private Texture Specular;
    private Texture Roughness;
    private Texture AO;
    private Texture Normal;
    private Texture Alpha;
    private Texture Emissive;

    private readonly bool TwoSided;
    
    public Material(GraphicsDevice graphicsDevice, CommandList commandList, ResourceLayout resourceLayout, UObject material)
    {
        GraphicsDevice = graphicsDevice;
        CommandList = commandList;
        
        var parameters = material.GetMaterialParameters();

        TwoSided = parameters.TwoSided;
        Color = FindTexture(resourceLayout, parameters, nameof(Color), new Vector4(0.5f));
        Metallic = FindTexture(resourceLayout, parameters, nameof(Metallic), Vector4.Zero);
        Specular = FindTexture(resourceLayout, parameters, nameof(Specular), new Vector4(0.5f));
        Roughness = FindTexture(resourceLayout, parameters, nameof(Roughness), new Vector4(0.5f));
        AO = FindTexture(resourceLayout, parameters, nameof(AO), Vector4.One);
        Normal = FindTexture(resourceLayout, parameters, nameof(Normal), new Vector4(0.5f, 0.5f, 1, 1));
        Alpha = FindTexture(resourceLayout, parameters, nameof(Alpha), Vector4.One);
        Emissive = FindTexture(resourceLayout, parameters, nameof(Emissive), Vector4.Zero);
    }

    private Texture FindTexture(ResourceLayout resourceLayout, MaterialParameters parameters, string parameterName, Vector4 defaultColor)
    {
        var item = RenderContext.AutoTextureItems.FirstOrDefault(item => item.Parameter == parameterName);
        if (item != null && !string.IsNullOrWhiteSpace(item.Regex))
        {
            var regex = new Regex(item.Regex, RegexOptions.IgnoreCase);
            foreach (var texParam in parameters.Textures)
                if (regex.IsMatch(texParam.Key))
                    return ResourceCache.Textures.GetOrAdd(texParam.Value.Owner!.Name, ()=> new Texture(GraphicsDevice, resourceLayout, texParam.Value));
            foreach (var texParam in parameters.ReferenceTextures)
                if (regex.IsMatch(texParam.Key))
                    return ResourceCache.Textures.GetOrAdd(texParam.Value.Owner!.Name, ()=> new Texture(GraphicsDevice, resourceLayout, texParam.Value));
        }
        return ResourceCache.Textures.GetOrAdd("Fallback_" + parameterName, ()=> new Texture(GraphicsDevice, resourceLayout, defaultColor));
    }

    public void Render()
    {
        CommandList.SetGraphicsResourceSet(3, Color.ResourceSet);
        CommandList.SetGraphicsResourceSet(4, Metallic.ResourceSet);
        CommandList.SetGraphicsResourceSet(5, Specular.ResourceSet);
        CommandList.SetGraphicsResourceSet(6, Roughness.ResourceSet);
        CommandList.SetGraphicsResourceSet(7, AO.ResourceSet);
        CommandList.SetGraphicsResourceSet(8, Normal.ResourceSet);
        CommandList.SetGraphicsResourceSet(9, Emissive.ResourceSet);
        CommandList.SetGraphicsResourceSet(10, Alpha.ResourceSet);
    }
    
    public void Dispose()
    {
        if(IsDisposed)
            return;
        
        Color.Dispose();
        Metallic.Dispose();
        Roughness.Dispose();
        AO.Dispose();
        Normal.Dispose();

        IsDisposed = true;
    }
}