using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using UniversalUmap.Rendering.Inspector;

namespace UniversalUmap.Rendering.Controls;

/// <summary>Details-panel asset-reference chip (icon + name), styled like Unreal's own object-reference rows - the widget shown for resolved UObject references.</summary>
public partial class ObjectReferenceControl : UserControl
{
    private DetailItem? currentItem;

    public ObjectReferenceControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (currentItem is not null)
            currentItem.PropertyChanged -= OnItemPropertyChanged;

        currentItem = DataContext as DetailItem;

        if (currentItem is not null)
            currentItem.PropertyChanged += OnItemPropertyChanged;

        Refresh();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(DetailItem.ObjectReferenceValue))
            Refresh();
    }

    private void Refresh()
    {
        var reference = currentItem?.ObjectReferenceValue;

        NameText.Text = reference?.Name ?? "None";
        GlyphText.Text = string.IsNullOrEmpty(reference?.ClassName) ? "?" : reference.ClassName[..1].ToUpperInvariant();
        AccentBar.Background = new SolidColorBrush(reference?.AccentColor ?? Colors.Gray);
        ToolTip.SetTip(this, reference?.Path);
    }
}
