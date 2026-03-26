using System;
using System.Collections.Specialized;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;
using UniversalUmap.Rendering.Scenes;

namespace UniversalUmap.Rendering.ViewModels;

public sealed partial class OutlinerPanelViewModel : ViewModelBase
{
    [ObservableProperty] private SuppressibleObservableCollection<OutlinerItem> items = [];
    [ObservableProperty] private OutlinerItem? selectedItem;
    [ObservableProperty] private bool isSceneEmpty = true;

    private Scene? scene;
    private bool syncingSelectionFromScene;
    private OutlinerItem? selectedItemRef;

    private const int ItemsPerBatch = 5;

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

        var count = scene.GetInstanceCount();
        var startIndex = Items.Count;

        if (startIndex >= count)
            return;

        Dispatcher.UIThread.Post(() => AddItemsBatched(startIndex, count), DispatcherPriority.Background);
    }

    private void AddItemsBatched(int startIndex, int totalCount)
    {
        var added = 0;
        for (var i = startIndex; i < totalCount && added < ItemsPerBatch; i++)
        {
            try
            {
                if (scene!.TryGetInstanceAt(i, out var instance) && instance is not null)
                {
                    var subtitle = instance.MeshAsset?.Name ?? string.Empty;
                    var item = new OutlinerItem(i, instance.Name ?? $"Instance {i}", subtitle, SceneHierarchyNodeKind.StaticMesh);
                    Items.Add(item);
                }
                added++;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to add outliner item at index {Index}", i);
            }
        }

        if (added >= ItemsPerBatch)
        {
            Dispatcher.UIThread.Post(() => AddItemsBatched(startIndex + added, totalCount), DispatcherPriority.Background);
        }
        else if (Items.Count > 0)
        {
            SyncSelectionFromScene();
        }
    }

    public void Clear()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Items.Clear();
            SelectedItem = null;
        }, DispatcherPriority.Background);
    }

    partial void OnItemsChanged(SuppressibleObservableCollection<OutlinerItem>? oldValue, SuppressibleObservableCollection<OutlinerItem> newValue)
    {
        if (oldValue is not null)
            oldValue.CollectionChanged -= OnItemsCollectionChanged;

        newValue.CollectionChanged -= OnItemsCollectionChanged;
        newValue.CollectionChanged += OnItemsCollectionChanged;
        UpdateIsSceneEmpty();
    }

    partial void OnSelectedItemChanged(OutlinerItem? value)
    {
        UpdateSelectedNode(value);

        if (syncingSelectionFromScene || scene is null)
            return;

        if (value is not null && value.InstanceIndex >= 0)
            scene.SelectInstance(value.InstanceIndex);
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

            if (selectedInstanceIndex < 0 || selectedInstanceIndex >= Items.Count)
                SelectedItem = null;
            else if (!ReferenceEquals(SelectedItem, Items[selectedInstanceIndex]))
                SelectedItem = Items[selectedInstanceIndex];
        }
        finally
        {
            syncingSelectionFromScene = false;
        }
    }

    private void UpdateSelectedNode(OutlinerItem? value)
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
