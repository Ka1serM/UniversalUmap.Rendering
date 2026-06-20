using System;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
namespace UniversalUmap.Rendering.Scenes;

public partial class EnvironmentSettings : ObservableObject
    , IGpuSnapshot<EnvironmentDataGpu>
{
    [ObservableProperty] private int textureIndex = -1;
    [ObservableProperty] private int cdfTextureIndex = -1;
    [ObservableProperty] private float rotation;
    [ObservableProperty] private float visibleExposure = 1f;
    [ObservableProperty] private float lightingExposure = 0f;
    [ObservableProperty] private float maxTextureLod;
    [ObservableProperty] private bool visible = true;
    [ObservableProperty] private Vector3 directionalDirection = new(0.41338775f, -0.7398497f, 0.53078514f);
    [ObservableProperty] private float directionalIntensity = 6f;
    [ObservableProperty] private float directionalSoftAngle;

    EnvironmentDataGpu IGpuSnapshot<EnvironmentDataGpu>.ToStruct() => ToStruct();

    internal EnvironmentDataGpu ToStruct()
    {
        return new EnvironmentDataGpu
        {
            TextureIndex = TextureIndex,
            CdfTextureIndex = CdfTextureIndex,
            RotationSin = MathF.Sin(Rotation * (MathF.PI / 180f)),
            RotationCos = MathF.Cos(Rotation * (MathF.PI / 180f)),
            VisibleExposureScale = MathF.Pow(2f, VisibleExposure),
            LightingExposureScale = MathF.Pow(2f, LightingExposure),
            MaxTextureLod = MaxTextureLod,
            Visible = Visible ? 1 : 0,
            DirectionalDirection = DirectionalDirection,
            DirectionalIntensity = DirectionalIntensity,
            Rotation = Rotation,
            VisibleExposure = VisibleExposure,
            LightingExposure = LightingExposure,
            DirectionalSoftAngle = DirectionalSoftAngle
        };
    }
}
