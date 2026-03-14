using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UniversalUmap.Rendering.Scenes;

namespace UniversalUmap.Rendering.ViewModels;

public sealed partial class MapsOutlinerPanelViewModel : ViewModelBase
{
    [ObservableProperty] private SuppressibleObservableCollection<SceneHierarchyNode> items = [];
    [ObservableProperty] private int itemCount;
    [ObservableProperty] private bool isEmpty = true;
    [ObservableProperty] private SceneHierarchyNode? selectedItem;

    private Scene? scene;
    private bool syncingSelectionFromScene;
    private SceneHierarchyNode? selectedNode;

    public void SetScene(Scene? scene)
    {
        if (ReferenceEquals(this.scene, scene))
            return;

        if (this.scene is not null)
            this.scene.SelectedInstanceChanged -= OnSceneSelectedInstanceChanged;

        this.scene = scene;

        if (this.scene is not null)
            this.scene.SelectedInstanceChanged += OnSceneSelectedInstanceChanged;

        Refresh();
    }

    public void Refresh()
    {
        var roots = scene?.GetHierarchyRootsSnapshot().ToList() ?? [];

        Dispatcher.UIThread.Post(() =>
        {
            Items.Clear();
            if (roots.Count > 0)
                Items.AddRangeSuppressed(roots);

            ItemCount = CountItems(Items);
            IsEmpty = ItemCount == 0;
            SyncSelectionFromScene();
        });
    }

    partial void OnSelectedItemChanged(SceneHierarchyNode? value)
    {
        UpdateSelectedNode(value);

        if (syncingSelectionFromScene || scene is null)
            return;

        if (TryGetPreferredInstanceIndex(value, out var instanceIndex))
            scene.SelectInstance(instanceIndex);
        else
            scene.ClearSelection();
    }

    private void OnSceneSelectedInstanceChanged(int selectedInstanceIndex)
    {
        Dispatcher.UIThread.Post(SyncSelectionFromScene);
    }

    private void SyncSelectionFromScene()
    {
        syncingSelectionFromScene = true;
        try
        {
            var selectedInstanceIndex = scene?.SelectedInstanceIndex ?? -1;
            SelectedItem = FindAndExpandNodeByInstanceIndex(Items, selectedInstanceIndex);
        }
        finally
        {
            syncingSelectionFromScene = false;
        }
    }

    private static int CountItems(IEnumerable<SceneHierarchyNode> nodes)
    {
        var total = 0;
        foreach (var node in nodes)
        {
            total++;
            if (node.Children.Count > 0)
                total += CountItems(node.Children);
        }

        return total;
    }

    private static SceneHierarchyNode? FindAndExpandNodeByInstanceIndex(IEnumerable<SceneHierarchyNode> nodes, int instanceIndex)
    {
        if (instanceIndex < 0)
            return null;

        foreach (var node in nodes)
        {
            if (node.MatchesInstanceIndex(instanceIndex))
                return node;

            if (node.Children.Count <= 0)
                continue;

            var childMatch = FindAndExpandNodeByInstanceIndex(node.Children, instanceIndex);
            if (childMatch is not null)
            {
                node.IsExpanded = true;
                return childMatch;
            }
        }

        return null;
    }

    private void UpdateSelectedNode(SceneHierarchyNode? value)
    {
        if (ReferenceEquals(selectedNode, value))
        {
            if (selectedNode is not null && !selectedNode.IsSelected)
                selectedNode.IsSelected = true;
            return;
        }

        if (selectedNode is not null)
            selectedNode.IsSelected = false;

        selectedNode = value;

        if (selectedNode is not null)
            selectedNode.IsSelected = true;
    }

    private static bool TryGetPreferredInstanceIndex(SceneHierarchyNode? node, out int instanceIndex)
    {
        if (node is null)
        {
            instanceIndex = -1;
            return false;
        }

        if (node.InstanceIndexCount > 0)
        {
            instanceIndex = node.InstanceIndexStart;
            return true;
        }

        foreach (var child in node.Children)
        {
            if (TryGetPreferredInstanceIndex(child, out instanceIndex))
                return true;
        }

        instanceIndex = -1;
        return false;
    }
}
