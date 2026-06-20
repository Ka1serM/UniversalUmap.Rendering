using System.Numerics;
using UniversalUmap.Rendering.Inspector;
using UniversalUmap.Rendering.Vulkan;

namespace UniversalUmap.Rendering.Scenes;

public sealed class InspectableMaterial : IInspectable
{
    private readonly MaterialData data;

    public InspectableMaterial(int index, MaterialData data)
    {
        Index = index;
        this.data = data;
    }

    public int Index { get; }

    public string InspectorTitle => $"Material {Index}";

    [Detail("Albedo", Group = "Surface", Order = 0)]
    public Vector3 Albedo => data.Albedo;

    [Detail("Metallic", Group = "Surface", Order = 1, Format = "0.00")]
    public float Metallic => data.Metallic;

    [Detail("Roughness", Group = "Surface", Order = 2, Format = "0.00")]
    public float Roughness => data.Roughness;

    [Detail("Specular", Group = "Surface", Order = 3, Format = "0.00")]
    public float Specular => data.Specular;

    [Detail("IOR", Group = "Surface", Order = 4, Format = "0.00")]
    public float Ior => data.Ior;

    [Detail("Emission", Group = "Emission", Order = 0)]
    public Vector3 Emission => data.Emission;

    [Detail("Emission Strength", Group = "Emission", Order = 1, Format = "0.00")]
    public float EmissionStrength => data.EmissionStrength;

    [Detail("Opacity", Group = "Transparency", Order = 0, Format = "0.00")]
    public float Opacity => data.Opacity;

    [Detail("Transmission", Group = "Transparency", Order = 1, Format = "0.00")]
    public float Transmission => data.Transmission;
}
