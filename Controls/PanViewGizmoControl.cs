using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentIcons.Avalonia.Fluent;
using FluentIcons.Common;

namespace UniversalUmap.Rendering.Controls;

public sealed class PanViewGizmoControl : ContentControl
{
    private const float HoverScaleBoost = 0.06f;
    private static readonly Color FillColor = Color.FromArgb(166, 58, 58, 58);
    private static readonly Color HoverColor = Color.FromArgb(200, 72, 72, 72);
    private static readonly IBrush FillBrush = new SolidColorBrush(FillColor);
    private static readonly IBrush HoverBrush = new SolidColorBrush(HoverColor);
    private static readonly IBrush PressBrush = new SolidColorBrush(Color.FromArgb(220, 92, 92, 92));

    private readonly Border chrome;
    private readonly ScaleTransform hoverScale = new(1, 1);
    private bool isHovering;
    public static readonly StyledProperty<VulkanViewerControl?> SourceProperty =
        AvaloniaProperty.Register<PanViewGizmoControl, VulkanViewerControl?>(nameof(Source));

    public VulkanViewerControl? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public PanViewGizmoControl()
    {
        Width = 38;
        Height = 38;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        Margin = new Thickness(0, 196, 15, 0);
        RenderTransformOrigin = RelativePoint.Center;
        RenderTransform = hoverScale;

        chrome = new Border
        {
            CornerRadius = new CornerRadius(19),
            Background = FillBrush,
            Child = new SymbolIcon
            {
                Symbol = Symbol.HandDraw,
                FontSize = 19,
                Foreground = Brushes.White,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            }
        };
        Content = chrome;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SharedPointerCapture.End(this);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        isHovering = true;
        ApplyVisualState();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (SharedPointerCapture.IsOwnedBy(this))
            return;
        isHovering = false;
        ApplyVisualState();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed)
            return;

        SharedPointerCapture.TryBegin(this, e.Pointer, e.GetPosition(this));
        ApplyVisualState();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!SharedPointerCapture.IsOwnedBy(this))
            return;

        if (SharedPointerCapture.TryConsumeWarpSuppressedMove(this))
        {
            e.Handled = true;
            return;
        }

        var viewer = Source;
        if (viewer?.Scene is null)
            return;

        var position = e.GetPosition(this);
        var delta = SharedPointerCapture.GetDelta(this, position);
        if (Math.Abs(delta.X) > double.Epsilon || Math.Abs(delta.Y) > double.Epsilon)
            viewer.Scene.Mutate(scene => scene.CameraController.PanInViewPlane((float)delta.X, (float)delta.Y));

        SharedPointerCapture.TryWrapAround(this, position);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!SharedPointerCapture.IsOwnedBy(this))
            return;

        SharedPointerCapture.End(this, e.Pointer);
        isHovering = Bounds.Contains(e.GetPosition(this));
        ApplyVisualState();
        e.Handled = true;
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        SharedPointerCapture.End(this);
        isHovering = false;
        ApplyVisualState();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        SharedPointerCapture.End(this);
        isHovering = false;
        ApplyVisualState();
    }

    private void ApplyVisualState()
    {
        if (SharedPointerCapture.IsOwnedBy(this))
        {
            chrome.Background = PressBrush;
            hoverScale.ScaleX = 1d + HoverScaleBoost;
            hoverScale.ScaleY = 1d + HoverScaleBoost;
            return;
        }

        chrome.Background = isHovering ? HoverBrush : FillBrush;
        var s = 1d + (isHovering ? HoverScaleBoost : 0d);
        hoverScale.ScaleX = s;
        hoverScale.ScaleY = s;
    }

}
