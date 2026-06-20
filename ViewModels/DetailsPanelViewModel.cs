using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UniversalUmap.Rendering.Inspector;
using UniversalUmap.Rendering.Scenes;

namespace UniversalUmap.Rendering.ViewModels;

public sealed partial class DetailsPanelViewModel : ViewModelBase
{
    private Scene? scene;
    private object? currentSource;
    private readonly List<DetailItem> liveItems = [];

    [ObservableProperty] private ObservableCollection<DetailGroup> groups = [];
    [ObservableProperty] private string title = "Nothing selected";
    [ObservableProperty] private bool hasDetails;

    public void SetScene(Scene? newScene)
    {
        if (ReferenceEquals(scene, newScene))
            return;

        if (scene is not null)
        {
            scene.SelectedHierarchyNodeChanged -= OnSelectionChanged;
            scene.SelectedInstanceChanged -= OnSelectionChanged;
            scene.CameraChanged -= OnCameraChanged;
        }

        scene = newScene;

        if (scene is not null)
        {
            scene.SelectedHierarchyNodeChanged += OnSelectionChanged;
            scene.SelectedInstanceChanged += OnSelectionChanged;
            scene.CameraChanged += OnCameraChanged;
        }

        QueueRebuild();
    }

    private void OnSelectionChanged(int _) => QueueRebuild();

    private void OnCameraChanged()
    {
        if (currentSource is CameraBase)
            QueueRebuild();
    }

    private void QueueRebuild()
    {
        Dispatcher.UIThread.Post(Rebuild, DispatcherPriority.Background);
    }

    private void Rebuild()
    {
        var source = scene?.ResolveSelectedInspectable();
        currentSource = source;

        foreach (var item in liveItems)
            item.Dispose();
        liveItems.Clear();

        var newGroups = new ObservableCollection<DetailGroup>();
        foreach (var group in InspectorBuilder.Build(source))
        {
            newGroups.Add(group);
            liveItems.AddRange(group.Items);
        }

        Groups = newGroups;
        HasDetails = newGroups.Count > 0;
        Title = (source as IInspectable)?.InspectorTitle ?? "Nothing selected";
    }
}
