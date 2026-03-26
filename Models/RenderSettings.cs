using CommunityToolkit.Mvvm.ComponentModel;
namespace UniversalUmap.Rendering.Scenes;

public partial class RenderSettings : ObservableObject
    , IGpuSnapshot<RenderSettingsDataGpu>
{
    [ObservableProperty] private int samplesPerPixel = 1;
    [ObservableProperty] private int pathDepth = 4;
    [ObservableProperty] private bool applyPixelSizeOnlyWhileMoving;
    [ObservableProperty] private bool adaptiveSamplingEnabled = true;
    [ObservableProperty] private int adaptiveMinSamples = 4;
    [ObservableProperty] private float adaptiveTargetError = 0.03f;
    [ObservableProperty] private int russianRouletteStartBounce = 3;
    [ObservableProperty] private BufferVisualizationMode bufferVisualization = BufferVisualizationMode.FinalColor;
    [ObservableProperty] private float exposure = 0f;
    [ObservableProperty] private bool transparentBackground;
    [ObservableProperty] private RenderMode renderMode = RenderMode.AmbientOcclusion;
    [ObservableProperty] private RenderPixelSize pixelSize = RenderPixelSize.X1;
    [ObservableProperty] private bool aoSampleAlbedo = true;

    RenderSettingsDataGpu IGpuSnapshot<RenderSettingsDataGpu>.ToStruct() => ToStruct();

    internal RenderSettingsDataGpu ToStruct()
    {
        var clampedSamples = SamplesPerPixel < 1 ? 1 : SamplesPerPixel;
        var clampedPathDepth = PathDepth < 1 ? 1 : PathDepth;
        var clampedAdaptiveMinSamples = AdaptiveMinSamples < 1 ? 1 : AdaptiveMinSamples;
        var clampedAdaptiveTargetError = AdaptiveTargetError < 0f ? 0f : AdaptiveTargetError;
        var clampedRussianRouletteStartBounce = RussianRouletteStartBounce < 1 ? 1 : RussianRouletteStartBounce;

        return new RenderSettingsDataGpu
        {
            Samples = clampedSamples,
            DiffuseBounces = clampedPathDepth,
            SpecularBounces = clampedPathDepth,
            TransmissionBounces = clampedPathDepth,
            AdaptiveSamplingEnabled = AdaptiveSamplingEnabled ? 1 : 0,
            AdaptiveMinSamples = clampedAdaptiveMinSamples,
            AdaptiveTargetError = clampedAdaptiveTargetError,
            RussianRouletteStartBounce = clampedRussianRouletteStartBounce,
            Exposure = Exposure,
            TransparentBackground = TransparentBackground ? 1 : 0,
            RenderMode = (int)RenderMode,
            AoSampleAlbedo = AoSampleAlbedo ? 1 : 0
        };
    }
}
