using UniversalUmap.Rendering.Scenes;

namespace UniversalUmap.Rendering.ViewModels;

public sealed class MapsRenderSettingsPanelViewModel : ScenePanelViewModel<RenderSettings>
{
    public IReadOnlyList<RenderMode> RenderModes { get; } = Enum.GetValues<RenderMode>();
    public IReadOnlyList<BufferVisualizationMode> BufferVisualizationModes { get; } = Enum.GetValues<BufferVisualizationMode>();

    protected override RenderSettings? ResolveModel(Scene? scene)
    {
        return scene?.RenderSettings;
    }
}
