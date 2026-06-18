using System.Collections.Specialized;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UniversalUmap.Rendering.Scenes;

namespace UniversalUmap.Rendering.ViewModels;

public sealed partial class OutlinerPanelViewModel : ViewModelBase
{
    [ObservableProperty] private SuppressibleObservableCollection<SceneHierarchyNode> items = [];
    [ObservableProperty] private SceneHierarchyNode? selectedItem;
    [ObservableProperty] private bool isSceneEmpty = true;

    private Scene? scene;
    private bool syncingSelectionFromScene;
    private SceneHierarchyNode? selectedItemRef;

    public OutlinerPanelViewModel()
    {
        Items.CollectionChanged += OnItemsCollectionChanged;
        UpdateIsSceneEmpty();
    }

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
        if (scene is null)
            return;

        var roots = scene.GetHierarchyRootsSnapshot();
        Dispatcher.UIThread.Post(() =>
        {
            Items.Clear();
            foreach (var root in roots)
                Items.Add(root);
            SyncSelectionFromScene();
        }, DispatcherPriority.Background);
    }

    public void Clear()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Items.Clear();
            SelectedItem = null;
        }, DispatcherPriority.Background);
    }

    partial void OnItemsChanged(SuppressibleObservableCollection<SceneHierarchyNode>? oldValue, SuppressibleObservableCollection<SceneHierarchyNode> newValue)
    {
        if (oldValue is not null)
            oldValue.CollectionChanged -= OnItemsCollectionChanged;

        newValue.CollectionChanged -= OnItemsCollectionChanged;
        newValue.CollectionChanged += OnItemsCollectionChanged;
        UpdateIsSceneEmpty();
    }

    partial void OnSelectedItemChanged(SceneHierarchyNode? value)
    {
        UpdateSelectedNode(value);

        if (syncingSelectionFromScene || scene is null)
            return;

        var instanceIndex = value?.FirstInstanceIndexOrDefault() ?? -1;
        if (instanceIndex >= 0)
            scene.SelectInstance(instanceIndex);
        else
            scene.ClearSelection();
    }

    private void OnSceneSelectedInstanceChanged(int selectedInstanceIndex)
    {
        Dispatcher.UIThread.Post(SyncSelectionFromScene, DispatcherPriority.Background);
    }

    private void SyncSelectionFromScene()
    {
        if (syncingSelectionFromScene || scene is null)
            return;

        syncingSelectionFromScene = true;
        try
        {
            var selectedInstanceIndex = scene.SelectedInstanceIndex;

            if (selectedInstanceIndex < 0 || !scene.TryGetHierarchyNodeForInstance(selectedInstanceIndex, out var node))
                SelectedItem = null;
            else if (!ReferenceEquals(SelectedItem, node))
                SelectedItem = node;
        }
        finally
        {
            syncingSelectionFromScene = false;
        }
    }

    private void UpdateSelectedNode(SceneHierarchyNode? value)
    {
        if (ReferenceEquals(selectedItemRef, value))
        {
            if (selectedItemRef is not null && !selectedItemRef.IsSelected)
                selectedItemRef.IsSelected = true;
            return;
        }

        if (selectedItemRef is not null)
            selectedItemRef.IsSelected = false;

        selectedItemRef = value;

        if (selectedItemRef is not null)
            selectedItemRef.IsSelected = true;
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateIsSceneEmpty();
    }

    private void UpdateIsSceneEmpty()
    {
        IsSceneEmpty = Items.Count == 0;
    }
}
