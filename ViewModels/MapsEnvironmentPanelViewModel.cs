using UniversalUmap.Rendering.Scenes;

namespace UniversalUmap.Rendering.ViewModels;

public sealed class MapsEnvironmentPanelViewModel : ScenePanelViewModel<EnvironmentSettings>
{
    protected override EnvironmentSettings? ResolveModel(Scene? scene)
    {
        return scene?.Environment;
    }
}
