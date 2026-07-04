using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using UniversalUmap.Rendering.Inspector;

namespace UniversalUmap.Rendering.Controls;

/// <summary>Details-panel color swatch that opens an RGBA picker popup - the widget shown for FColor/FLinearColor DetailItems.</summary>
public partial class ColorFieldControl : UserControl
{
    private DetailItem? currentItem;
    private bool suppressFieldEvents;

    public ColorFieldControl()
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

        RefreshSwatch();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(DetailItem.ColorValue))
            RefreshSwatch();
    }

    private void RefreshSwatch()
    {
        if (currentItem is null)
            return;

        var color = currentItem.ColorValue;
        Swatch.Background = new SolidColorBrush(color);

        suppressFieldEvents = true;
        RedField.Value = color.R;
        GreenField.Value = color.G;
        BlueField.Value = color.B;
        AlphaField.Value = color.A;
        suppressFieldEvents = false;
    }

    private void ColorField_OnValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (suppressFieldEvents || currentItem is null || currentItem.IsReadOnlyValue)
            return;

        currentItem.ColorValue = Color.FromArgb(
            (byte)(AlphaField.Value ?? 255),
            (byte)(RedField.Value ?? 0),
            (byte)(GreenField.Value ?? 0),
            (byte)(BlueField.Value ?? 0));
    }

    private void SwatchButton_OnClick(object? sender, RoutedEventArgs e)
    {
        PickerPopup.IsOpen = !PickerPopup.IsOpen;
    }
}
