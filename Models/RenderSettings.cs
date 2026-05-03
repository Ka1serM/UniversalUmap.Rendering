using CommunityToolkit.Mvvm.ComponentModel;
namespace UniversalUmap.Rendering.Scenes;

public partial class RenderSettings : ObservableObject
    , IGpuSnapshot<RenderSettingsDataGpu>
{
    [ObservableProperty] private int samplesPerPixel = 1;
    [ObservableProperty] private int pathDepth = 3;
    [ObservableProperty] private bool applyPixelSizeOnlyWhileMoving = true;
    [ObservableProperty] private bool adaptiveSamplingEnabled = false;
    [ObservableProperty] private int adaptiveMinSamples = 4;
    [ObservableProperty] private float adaptiveTargetError = 0.03f;
    [ObservableProperty] private int russianRouletteStartBounce = 3;
    [ObservableProperty] private BufferVisualizationMode bufferVisualization = BufferVisualizationMode.FinalColor;
    [ObservableProperty] private float exposure = 0f;
    [ObservableProperty] private bool transparentBackground = false;
    [ObservableProperty] private RenderMode renderMode = RenderMode.AmbientOcclusion;
    [ObservableProperty] private RenderPixelSize pixelSize = RenderPixelSize.X2;
    [ObservableProperty] private bool aoSampleAlbedo = true;
    [ObservableProperty] private bool rasterRtAmbientOcclusionEnabled = false;
    [ObservableProperty] private bool rasterRtShadowsEnabled = false;
    [ObservableProperty] private bool rasterSsilEnabled = true;
    [ObservableProperty] private bool rasterVirtualShadowMapsEnabled = true;
    [ObservableProperty] private bool rasterScreenSpaceContactShadowsEnabled = true;
    [ObservableProperty] private bool rasterScreenSpaceReflectionsEnabled = true;

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
            AoSampleAlbedo = AoSampleAlbedo ? 1 : 0,
            RasterRtAmbientOcclusionEnabled = RasterRtAmbientOcclusionEnabled ? 1 : 0,
            RasterRtShadowsEnabled = RasterRtShadowsEnabled ? 1 : 0,
            RasterSsilEnabled = RasterSsilEnabled ? 1 : 0,
            RasterVirtualShadowMapsEnabled = RasterVirtualShadowMapsEnabled ? 1 : 0,
            RasterScreenSpaceContactShadowsEnabled = RasterScreenSpaceContactShadowsEnabled ? 1 : 0,
            RasterScreenSpaceReflectionsEnabled = RasterScreenSpaceReflectionsEnabled ? 1 : 0
        };
    }
}
