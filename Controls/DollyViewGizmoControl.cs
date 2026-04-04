using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace UniversalUmap.Rendering.Controls;

public sealed class DollyViewGizmoControl : Button
{
    private VulkanViewerControl? viewer;

    public static readonly StyledProperty<VulkanViewerControl?> SourceProperty =
        AvaloniaProperty.Register<DollyViewGizmoControl, VulkanViewerControl?>(nameof(Source));

    public VulkanViewerControl? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public DollyViewGizmoControl()
    {
        Classes.Add("iconSquare");
        Classes.Add("lift");
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        Margin = new Thickness(0, 148, 15, 0);
        Content = new FluentIcons.Avalonia.Fluent.SymbolIcon
        {
            Symbol = FluentIcons.Common.Symbol.Search,
            FontSize = 19
        };
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (PointerCaptureCoordinator.IsOwnedBy(this))
            PointerCaptureCoordinator.End(this);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed || props.IsRightButtonPressed)
        {
            if (PointerCaptureCoordinator.TryBegin(this, e.Pointer, e.GetPosition(this)))
            {
                e.Handled = true;
                return;
            }
        }

        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (PointerCaptureCoordinator.IsOwnedBy(this))
        {
            if (PointerCaptureCoordinator.TryConsumeWarpSuppressedMove(this))
            {
                e.Handled = true;
                return;
            }

            if (TryGetActiveRenderer(out var activeRenderer))
            {
                var position = e.GetPosition(this);
                var delta = PointerCaptureCoordinator.GetDelta(this, position);
                if (Math.Abs(delta.Y) > double.Epsilon)
                    activeRenderer.Scene!.DollyCamera((float)(-delta.Y * 0.05));

                PointerCaptureCoordinator.TryWrapAround(this, position);
            }

            e.Handled = true;
            return;
        }

        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (PointerCaptureCoordinator.IsOwnedBy(this))
        {
            PointerCaptureCoordinator.End(this, e.Pointer);
            e.Handled = true;
            return;
        }

        base.OnPointerReleased(e);
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        if (PointerCaptureCoordinator.IsOwnedBy(this))
            PointerCaptureCoordinator.End(this);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (PointerCaptureCoordinator.IsOwnedBy(this))
            PointerCaptureCoordinator.End(this);
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
