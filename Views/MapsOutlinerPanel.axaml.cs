using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniversalUmap.Rendering.ViewModels;

namespace UniversalUmap.Rendering.Views;

public partial class MapsOutlinerPanel : UserControl
{
    private const int MaxSelectionSyncAttempts = 4;
    private MapsOutlinerPanelViewModel? viewModel;

    public MapsOutlinerPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (viewModel is not null)
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        viewModel = DataContext as MapsOutlinerPanelViewModel;

        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            SyncSelectionToTreeView();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MapsOutlinerPanelViewModel.SelectedItem) ||
            e.PropertyName == nameof(MapsOutlinerPanelViewModel.Items))
            Dispatcher.UIThread.Post(SyncSelectionToTreeView, DispatcherPriority.Background);
    }

    private void SyncSelectionToTreeView()
    {
        SyncSelectionToTreeView(MaxSelectionSyncAttempts);
    }

    private void SyncSelectionToTreeView(int remainingAttempts)
    {
        var treeView = this.FindControl<TreeView>("OutlinerTreeView");
        if (treeView is null || viewModel is null)
            return;

        var selectedItem = viewModel.SelectedItem;
        treeView.SelectedItem = selectedItem;

        if (selectedItem is null)
            return;

        var treeViewItem = treeView.GetVisualDescendants()
            .OfType<TreeViewItem>()
            .FirstOrDefault(item => ReferenceEquals(item.DataContext, selectedItem));

        if (treeViewItem is not null)
        {
            treeViewItem.BringIntoView();
            return;
        }

        if (remainingAttempts <= 0)
            return;

        Dispatcher.UIThread.Post(
            () => SyncSelectionToTreeView(remainingAttempts - 1),
            DispatcherPriority.Background);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (viewModel is not null)
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        base.OnDetachedFromVisualTree(e);
    }
}
