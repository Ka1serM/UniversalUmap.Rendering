using CommunityToolkit.Mvvm.ComponentModel;
namespace UniversalUmap.Rendering.Scenes;

public partial class RenderSettings : ObservableObject
    , IGpuSnapshot<RenderSettingsDataGpu>
{
    [ObservableProperty] private RenderMode renderMode = RenderMode.AmbientOcclusion;
    [ObservableProperty] private RenderPixelSize pixelSize = RenderPixelSize.X1;
    [ObservableProperty] private float exposure = 0f;
    [ObservableProperty] private bool transparentBackground = false;
    [ObservableProperty] private BufferVisualizationMode bufferVisualization = BufferVisualizationMode.FinalColor;
    [ObservableProperty] private bool taaEnabled = true;
    [ObservableProperty] private bool applyPixelSizeOnlyWhileMoving = false;
    [ObservableProperty] private int samplesPerPixel = 1;
    [ObservableProperty] private int pathDepth = 8;
    [ObservableProperty] private bool aoSampleAlbedo = false;

    RenderSettingsDataGpu IGpuSnapshot<RenderSettingsDataGpu>.ToStruct() => ToStruct();

    internal RenderSettingsDataGpu ToStruct()
    {
        return new RenderSettingsDataGpu
        {
            Samples = SamplesPerPixel,
            DiffuseBounces = 3,
            SpecularBounces = 3,
            TransmissionBounces = 3,
            AdaptiveSamplingEnabled = 0,
            AdaptiveMinSamples = 1,
            AdaptiveTargetError = 0,
            RussianRouletteStartBounce = 3,
            Exposure = Exposure,
            TransparentBackground = TransparentBackground ? 1 : 0,
            RenderMode = (int)RenderMode,
            BufferVisualization = (int)BufferVisualization,
            TaaEnabled = TaaEnabled ? 1 : 0,
            AoSampleAlbedo = AoSampleAlbedo ? 1 : 0,
        };
    }
}
