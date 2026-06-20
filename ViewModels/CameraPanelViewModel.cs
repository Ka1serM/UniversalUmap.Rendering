using UniversalUmap.Rendering.Scenes;
using UniversalUmap.Rendering.Vulkan;

namespace UniversalUmap.Rendering.ViewModels;

public sealed class CameraPanelViewModel : ScenePanelViewModel<CameraBase>
{
    protected override CameraBase? ResolveModel(Scene? scene)
    {
        return scene?.Camera;
    }

    protected override void OnSceneChanged(Scene? previousScene, Scene? scene)
    {
        if (previousScene is not null)
            previousScene.CameraChanged -= OnSceneCameraChanged;
        base.OnSceneChanged(previousScene, scene);
        if (scene is not null)
            scene.CameraChanged += OnSceneCameraChanged;
        RefreshCameraProperties();
    }

    private void OnSceneCameraChanged()
    {
        Model = Scene?.Camera;
        RefreshCameraProperties();
    }

    private void RefreshCameraProperties()
    {
        OnPropertyChanged(nameof(CameraType));
        OnPropertyChanged(nameof(SelectedProjection));
        OnPropertyChanged(nameof(IsPerspective));
        OnPropertyChanged(nameof(IsOrthographic));
        OnPropertyChanged(nameof(IsFisheye));
    }

    public CameraProjectionType CameraType =>
        Model?.Projection ?? CameraProjectionType.Perspective;

    public bool IsPerspective => CameraType == CameraProjectionType.Perspective;
    public bool IsOrthographic => CameraType == CameraProjectionType.Orthographic;
    public bool IsFisheye => CameraType == CameraProjectionType.Fisheye;

    public CameraProjectionType[] ProjectionTypes { get; } =
        [CameraProjectionType.Perspective, CameraProjectionType.Orthographic, CameraProjectionType.Fisheye];

    public CameraProjectionType SelectedProjection
    {
        get => CameraType;
        set
        {
            if (value != CameraType)
                SwitchProjection(value);
        }
    }

    public void SwitchProjection(CameraProjectionType type)
    {
        if (Model?.Projection == type)
            return;
        Scene?.SetCameraProjection(type);
    }
}
