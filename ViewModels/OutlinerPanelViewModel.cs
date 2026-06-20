using System.Collections.Specialized;
using System.Linq;
using System.Threading;
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
    private int refreshQueued;

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
        {
            this.scene.SelectedHierarchyNodeChanged -= OnSceneSelectedHierarchyNodeChanged;
            this.scene.HierarchyChanged -= OnSceneHierarchyChanged;
        }

        this.scene = scene;

        if (this.scene is not null)
        {
            this.scene.SelectedHierarchyNodeChanged += OnSceneSelectedHierarchyNodeChanged;
            this.scene.HierarchyChanged += OnSceneHierarchyChanged;
        }

        Refresh();
    }

    public void Refresh()
    {
        if (scene is null)
        {
            Clear();
            return;
        }

        var roots = scene.GetHierarchyRootsSnapshot();
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshOnUiThread(roots);
            return;
        }

        Dispatcher.UIThread.Post(() => RefreshOnUiThread(roots), DispatcherPriority.Background);
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

        if (value is null)
            scene.ClearSelection();
        else
            scene.SelectHierarchyNode(new SceneHierarchyHandle(value.Id));
    }

    private void OnSceneSelectedHierarchyNodeChanged(int selectedHierarchyNodeId)
    {
        Dispatcher.UIThread.Post(SyncSelectionFromScene, DispatcherPriority.Background);
    }

    private void OnSceneHierarchyChanged()
    {
        if (Interlocked.Exchange(ref refreshQueued, 1) == 1)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref refreshQueued, 0);
            if (scene is null)
            {
                Clear();
                return;
            }

            RefreshOnUiThread(scene.GetHierarchyRootsSnapshot());
        }, DispatcherPriority.Background);
    }

    private void RefreshOnUiThread(IReadOnlyList<SceneHierarchyNode> roots)
    {
        if (!Items.SequenceEqual(roots))
            Items.ReplaceAllSuppressed(roots.ToArray());

        SyncSelectionFromScene();
        UpdateIsSceneEmpty();
    }

    private void SyncSelectionFromScene()
    {
        if (syncingSelectionFromScene || scene is null)
            return;

        syncingSelectionFromScene = true;
        try
        {
            var selectedHierarchyNodeId = scene.SelectedHierarchyNodeId;

            if (selectedHierarchyNodeId < 0 || !scene.TryGetHierarchyNode(new SceneHierarchyHandle(selectedHierarchyNodeId), out var node))
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
