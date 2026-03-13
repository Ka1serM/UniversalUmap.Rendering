using CommunityToolkit.Mvvm.ComponentModel;

namespace UniversalUmap.Rendering;

public partial class RenderSettings : ObservableObject
    , IGpuSnapshot<RenderSettingsDataGpu>
{
    [ObservableProperty] private int samplesPerPixel = 1;
    [ObservableProperty] private int pathDepth = 1;
    [ObservableProperty] private float exposure;
    [ObservableProperty] private bool transparentBackground;
    [ObservableProperty] private RenderMode renderMode = RenderMode.PathTracing;

    RenderSettingsDataGpu IGpuSnapshot<RenderSettingsDataGpu>.ToStruct() => ToStruct();

    internal RenderSettingsDataGpu ToStruct()
    {
        var clampedSamples = SamplesPerPixel < 1 ? 1 : SamplesPerPixel;
        var clampedPathDepth = PathDepth < 1 ? 1 : PathDepth;

        return new RenderSettingsDataGpu
        {
            Samples = clampedSamples,
            DiffuseBounces = clampedPathDepth,
            SpecularBounces = clampedPathDepth,
            TransmissionBounces = clampedPathDepth,
            Exposure = Exposure,
            TransparentBackground = TransparentBackground ? 1 : 0,
            RenderMode = (int)RenderMode
        };
    }
}
