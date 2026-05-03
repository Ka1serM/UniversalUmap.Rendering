using UniversalUmap.Rendering.Scenes;

namespace UniversalUmap.Rendering.ViewModels;

public sealed class RenderSettingsPanelViewModel : ScenePanelViewModel<RenderSettings>
{
    public IReadOnlyList<RenderMode> RenderModes { get; } =
    [
        RenderMode.AmbientOcclusion,
        RenderMode.Rasterized,
        RenderMode.PathTracing
    ];
    public IReadOnlyList<RenderPixelSize> PixelSizes { get; } = Enum.GetValues<RenderPixelSize>();
    public IReadOnlyList<BufferVisualizationMode> BufferVisualizationModes { get; } = Enum.GetValues<BufferVisualizationMode>();

    protected override RenderSettings? ResolveModel(Scene? scene)
    {
        return scene?.RenderSettings;
    }
}
