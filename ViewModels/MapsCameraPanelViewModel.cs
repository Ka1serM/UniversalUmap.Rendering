using UniversalUmap.Rendering.Scenes;

namespace UniversalUmap.Rendering.ViewModels;

public sealed class MapsCameraPanelViewModel : ScenePanelViewModel<Camera>
{
    protected override Camera? ResolveModel(Scene? scene)
    {
        return scene?.Camera;
    }
}
