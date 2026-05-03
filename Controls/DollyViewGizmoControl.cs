using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace UniversalUmap.Rendering.Controls;

public sealed partial class DollyViewGizmoControl : UserControl
{
    private VulkanViewerControl? viewer;
    private Button? button;

    public static readonly StyledProperty<VulkanViewerControl?> SourceProperty =
        AvaloniaProperty.Register<DollyViewGizmoControl, VulkanViewerControl?>(nameof(Source));

    public VulkanViewerControl? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public DollyViewGizmoControl()
    {
        InitializeComponent();

        button = this.FindControl<Button>("PART_Button");
        if (button is not null)
        {
            button.AddHandler(InputElement.PointerPressedEvent, OnButtonPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            button.AddHandler(InputElement.PointerMovedEvent, OnButtonPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            button.AddHandler(InputElement.PointerReleasedEvent, OnButtonPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            button.AddHandler(InputElement.PointerCaptureLostEvent, OnButtonPointerCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
            button.LostFocus += OnButtonLostFocus;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EndCapture();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnButtonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (button is null)
            return;

        var props = e.GetCurrentPoint(button).Properties;
        if (props.IsLeftButtonPressed || props.IsRightButtonPressed)
            PointerCapture.TryBegin(button, e.Pointer, e.GetPosition(button));
    }

    private void OnButtonPointerMoved(object? sender, PointerEventArgs e)
    {
        if (button is null || !PointerCapture.IsOwnedBy(button))
            return;

        var position = e.GetPosition(button);
        if (TryGetActiveRenderer(out var activeRenderer))
        {
            var delta = PointerCapture.UpdateMove(button, position);
            if (Math.Abs(delta.Y) > double.Epsilon)
                activeRenderer.Scene!.DollyCamera((float)(-delta.Y * 0.05));
        }

        e.Handled = true;
    }

    private void OnButtonPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (button is not null && PointerCapture.IsOwnedBy(button))
            PointerCapture.End(button, e.Pointer);
    }

    private void OnButtonLostFocus(object? sender, RoutedEventArgs e)
    {
        EndCapture();
    }

    private void OnButtonPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        EndCapture();
    }

    private void EndCapture()
    {
        if (button is not null && PointerCapture.IsOwnedBy(button))
            PointerCapture.End(button);
    }

    private bool TryGetActiveRenderer(out VulkanViewerControl activeViewer)
    {
        var resolved = Source;
        if (resolved is not null && resolved.Scene is not null)
        {
            if (!ReferenceEquals(viewer, resolved))
                viewer = resolved;

            activeViewer = resolved;
            return true;
        }

        viewer = null;
        activeViewer = null!;
        return false;
    }
}
