using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniversalUmap.Rendering.ViewModels;

namespace UniversalUmap.Rendering.Views;

public partial class OutlinerPanel : UserControl
{
    private OutlinerPanelViewModel? viewModel;

    public OutlinerPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (viewModel is not null)
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        viewModel = DataContext as OutlinerPanelViewModel;

        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            SyncSelectionToTreeView();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OutlinerPanelViewModel.SelectedItem) ||
            e.PropertyName == nameof(OutlinerPanelViewModel.Items))
            Dispatcher.UIThread.Post(SyncSelectionToTreeView, DispatcherPriority.Background);
    }

    private void SyncSelectionToTreeView()
    {
        var treeView = this.FindControl<TreeView>("OutlinerTreeView");
        if (treeView is null || viewModel?.SelectedItem is null)
            return;

        treeView.SelectedItem = viewModel.SelectedItem;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (viewModel is not null)
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        base.OnDetachedFromVisualTree(e);
    }
}
