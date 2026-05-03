using Avalonia;
using Avalonia.Controls;
using UniversalUmap.Rendering.Scenes;

namespace UniversalUmap.Rendering.Controls;

public partial class RenderModeSwitcher : UserControl
{
    public static readonly StyledProperty<RenderMode> SelectedModeProperty =
        AvaloniaProperty.Register<RenderModeSwitcher, RenderMode>(
            nameof(SelectedMode),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public RenderMode SelectedMode
    {
        get => GetValue(SelectedModeProperty);
        set => SetValue(SelectedModeProperty, value);
    }

    public RenderModeSwitcher()
    {
        InitializeComponent();
        UpdateSelectionClasses();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SelectedModeProperty)
        {
            UpdateSelectionClasses();
        }
    }

    private void UpdateSelectionClasses()
    {
        AoButton.Classes.Set("selected", SelectedMode == RenderMode.AmbientOcclusion);
        RasterButton.Classes.Set("selected", SelectedMode == RenderMode.Rasterized);
        PathTracingButton.Classes.Set("selected", SelectedMode == RenderMode.PathTracing);
    }

    private void AoButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SelectedMode = RenderMode.AmbientOcclusion;
    }

    private void RasterButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SelectedMode = RenderMode.Rasterized;
    }

    private void PathTracingButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SelectedMode = RenderMode.PathTracing;
    }
}
