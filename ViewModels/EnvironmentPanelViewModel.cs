using UniversalUmap.Rendering.Scenes;

namespace UniversalUmap.Rendering.ViewModels;

public sealed class EnvironmentPanelViewModel : ScenePanelViewModel<EnvironmentSettings>
{
    protected override EnvironmentSettings? ResolveModel(Scene? scene)
    {
        return scene?.Environment;
    }
}
