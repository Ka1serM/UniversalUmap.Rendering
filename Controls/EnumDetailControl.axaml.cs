using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Layout;
using UniversalUmap.Rendering.Inspector;

namespace UniversalUmap.Rendering.Controls;

public partial class EnumDetailControl : UserControl
{
    private DetailItem? currentItem;

    public EnumDetailControl()
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
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(DetailItem.EnumValue))
            Refresh();
    }

    private void Refresh()
    {
        if (currentItem is null)
            return;

        if (currentItem.EnumValues.Count > 0)
        {
            EnumCombo.IsVisible = true;
            EnumText.IsVisible = false;
            return;
        }

        var raw = currentItem.GetRawValue();
        if (raw is string s)
        {
            var idx = s.IndexOf("::", StringComparison.Ordinal);
            if (idx >= 0)
            {
                EnumCombo.IsVisible = false;
                EnumText.IsVisible = true;
                EnumText.Text = s[(idx + 2)..];
            }
            else
            {
                EnumCombo.IsVisible = false;
                EnumText.IsVisible = true;
                EnumText.Text = s;
            }
        }
    }
}
