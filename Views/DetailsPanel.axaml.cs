using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using UniversalUmap.Rendering.Controls;
using UniversalUmap.Rendering.Inspector;
using UniversalUmap.Rendering.ViewModels;

namespace UniversalUmap.Rendering.Views;

public partial class DetailsPanel : UserControl
{
    private DetailsPanelViewModel? vm;

    public DetailsPanel()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (vm is not null)
        {
            vm.PropertyChanged -= OnVmPropertyChanged;
            vm.Groups.CollectionChanged -= OnGroupsChanged;
        }

        vm = DataContext as DetailsPanelViewModel;

        if (vm is not null)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            vm.Groups.CollectionChanged += OnGroupsChanged;
        }

        Rebuild();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DetailsPanelViewModel.Groups))
        {
            if (vm is not null)
                vm.Groups.CollectionChanged += OnGroupsChanged;
            Rebuild();
        }
    }

    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Rebuild();
    }

    private void Rebuild()
    {
        Container.Children.Clear();

        if (vm is null || vm.Groups.Count == 0)
        {
            PlaceholderText.IsVisible = true;
            ScrollViewer.IsVisible = false;
            return;
        }

        PlaceholderText.IsVisible = false;
        ScrollViewer.IsVisible = true;

        foreach (var group in vm.Groups)
            BuildGroup(group, Container);
    }

    private void BuildGroup(DetailGroup group, StackPanel parent)
    {
        if (string.IsNullOrEmpty(group.Name))
        {
            foreach (var entry in group.Entries)
            {
                if (entry is DetailItem item)
                    AddItemRow(item, parent);
                else if (entry is DetailGroup subGroup)
                    BuildGroup(subGroup, parent);
            }
            return;
        }

        var groupControl = new DetailGroupControl
        {
            GroupName = group.Name
        };

        foreach (var entry in group.Entries)
        {
            if (entry is DetailItem item)
                groupControl.AddItem(item.HideLabel ? null : item.Label, CreateEditorWithContext(item));
            else if (entry is DetailGroup subGroup)
                BuildGroup(subGroup, groupControl);
        }

        parent.Children.Add(groupControl);
    }

    private static void BuildGroup(DetailGroup group, DetailGroupControl parent)
    {
        var subGroupControl = new DetailGroupControl
        {
            GroupName = group.Name
        };

        foreach (var entry in group.Entries)
        {
            if (entry is DetailItem item)
                subGroupControl.AddItem(item.HideLabel ? null : item.Label, CreateEditorWithContext(item));
            else if (entry is DetailGroup subGroup)
                BuildGroup(subGroup, subGroupControl);
        }

        parent.AddSubGroup(subGroupControl);
    }

    private static void AddItemRow(DetailItem item, StackPanel parent)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(160, GridUnitType.Pixel),
                new ColumnDefinition(1, GridUnitType.Star)
            }
        };

        var editor = CreateEditorWithContext(item);

        if (!item.HideLabel)
        {
            var labelBlock = new TextBlock
            {
                Classes = { "renderLabel" },
                Text = item.Label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(labelBlock, 0);
            grid.Children.Add(labelBlock);
            Grid.SetColumn(editor, 1);
            editor.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        }
        else
        {
            Grid.SetColumnSpan(editor, 2);
            Grid.SetColumn(editor, 0);
        }

        grid.Children.Add(editor);
        parent.Children.Add(grid);
    }

    private static Control CreateEditorWithContext(DetailItem item)
    {
        var editor = CreateEditor(item);
        editor.DataContext = item;
        return editor;
    }

    private static Control CreateEditor(DetailItem item)
    {
        return item.EditorKind switch
        {
            DetailEditorKind.Slider => new SliderDetailControl(),
            DetailEditorKind.Enum => new EnumDetailControl(),
            DetailEditorKind.Toggle => new ToggleDetailControl(),
            DetailEditorKind.Number => new NumberDetailControl(),
            DetailEditorKind.Text => new TextDetailControl(),
            DetailEditorKind.Color => new ColorFieldControl(),
            DetailEditorKind.Vector => new VectorFieldControl(),
            DetailEditorKind.ObjectReference => new ObjectReferenceControl(),
            _ => new TextBlock { Text = item.DisplayValue, Classes = { "renderValue" } }
        };
    }
}
