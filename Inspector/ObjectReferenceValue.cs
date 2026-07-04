using Avalonia.Media;

namespace UniversalUmap.Rendering.Inspector;

/// <summary>A resolved Unreal object reference (mesh/material/texture/actor/...) shown as an asset-reference chip in the Details panel, styled like Unreal's own object-reference rows.</summary>
public sealed class ObjectReferenceValue
{
    public ObjectReferenceValue(string name, string className, string path)
    {
        Name = name;
        ClassName = className;
        Path = path;
        AccentColor = ResolveAccentColor(className);
    }

    public string Name { get; }
    public string ClassName { get; }
    public string Path { get; }
    public Color AccentColor { get; }

    private static Color ResolveAccentColor(string className) => className switch
    {
        "UStaticMesh" or "USkeletalMesh" => Color.FromRgb(0x3F, 0xC1, 0xC9),
        "UMaterial" or "UMaterialInstanceConstant" or "UMaterialInstanceDynamic" or "UMaterialInterface" => Color.FromRgb(0x4C, 0xAF, 0x50),
        "UTexture2D" or "UTextureCube" or "UTexture2DArray" or "UTexture" => Color.FromRgb(0xE3, 0x9C, 0x3C),
        _ => Color.FromRgb(0x6B, 0x8E, 0xE3)
    };
}
