using UniversalUmap.Rendering;

namespace UniversalUmap.Rendering.ViewModels;

public abstract class ScenePanelViewModel<TModel> : ViewModelBase
    where TModel : class
{
    private Scene? scene;
    private TModel? model;

    public Scene? Scene => scene;

    public TModel? Model
    {
        get => model;
        protected set => SetProperty(ref this.model, value);
    }

    public void SetScene(Scene? scene)
    {
        if (ReferenceEquals(this.scene, scene))
            return;

        var previousScene = this.scene;
        this.scene = scene;
        OnSceneChanged(previousScene, scene);
    }

    protected virtual void OnSceneChanged(Scene? previousScene, Scene? scene)
    {
        Model = ResolveModel(scene);
    }

    protected abstract TModel? ResolveModel(Scene? scene);
}
