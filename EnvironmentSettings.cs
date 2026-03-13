using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UniversalUmap.Rendering;

public partial class EnvironmentSettings : ObservableObject
    , IGpuSnapshot<EnvironmentDataGpu>
{
    [ObservableProperty] private int textureIndex = -1;
    [ObservableProperty] private int cdfTextureIndex = -1;
    [ObservableProperty] private float rotation;
    [ObservableProperty] private float visibleExposure = 2f;
    [ObservableProperty] private float lightingExposure = 2f;
    [ObservableProperty] private bool visible = true;
    [ObservableProperty] private Vector3 directionalDirection = new(0.41338775f, -0.7398497f, 0.53078514f);
    [ObservableProperty] private float directionalIntensity = 7f;

    EnvironmentDataGpu IGpuSnapshot<EnvironmentDataGpu>.ToStruct() => ToStruct();

    internal EnvironmentDataGpu ToStruct()
    {
        return new EnvironmentDataGpu
        {
            TextureIndex = TextureIndex,
            CdfTextureIndex = CdfTextureIndex,
            Rotation = Rotation,
            VisibleExposure = VisibleExposure,
            LightingExposure = LightingExposure,
            Visible = Visible ? 1 : 0,
            DirectionalDirection = DirectionalDirection,
            DirectionalIntensity = DirectionalIntensity,
            Pad0 = 0,
            Pad1 = 0
        };
    }
}
