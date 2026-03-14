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

public sealed class PanViewGizmoControl : CapturingControlBase
{
    private const float HoverScaleBoost = 0.06f;
    private static readonly Color FillColor = Color.FromArgb(196, 26, 26, 26);
    private static readonly Color HoverFillColor = Color.FromArgb(214, 82, 82, 82);
    private static readonly Color ActiveFillColor = Color.FromArgb(235, 104, 104, 104);
    private static readonly IBrush FillBrush = new SolidColorBrush(FillColor);
    private static readonly IBrush HoverBrush = new SolidColorBrush(HoverFillColor);
    private static readonly IBrush ActiveBrush = new SolidColorBrush(ActiveFillColor);
    private static readonly IBrush IconBrush = new SolidColorBrush(Color.FromArgb(245, 255, 255, 255));
    private readonly SymbolIcon icon;

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
            BorderThickness = new Thickness(0),
            Child = icon = new SymbolIcon
            {
                Symbol = Symbol.HandDraw,
                FontSize = 19,
                Foreground = IconBrush,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            }
        };
        Content = chrome;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EndCapture();
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
        if (IsCaptureActive)
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

        BeginCapture(e.Pointer, e.GetPosition(this));
        ApplyVisualState();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsCaptureActive)
            return;

        if (TryConsumeCaptureWarpMove())
        {
            e.Handled = true;
            return;
        }

        var viewer = Source;
        if (viewer?.Scene is null)
            return;

        var position = e.GetPosition(this);
        var delta = GetCaptureDelta(position);
        if (Math.Abs(delta.X) > double.Epsilon || Math.Abs(delta.Y) > double.Epsilon)
            viewer.Scene.PanCameraInViewPlane((float)delta.X, (float)delta.Y);

        TryWrapCapture(position);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!IsCaptureActive)
            return;

        EndCapture(e.Pointer);
        isHovering = Bounds.Contains(e.GetPosition(this));
        ApplyVisualState();
        e.Handled = true;
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        EndCapture();
        isHovering = false;
        ApplyVisualState();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndCapture();
        isHovering = false;
        ApplyVisualState();
    }

    private void ApplyVisualState()
    {
        if (IsCaptureActive)
        {
            chrome.Background = ActiveBrush;
            icon.Foreground = IconBrush;
            hoverScale.ScaleX = 1d + HoverScaleBoost;
            hoverScale.ScaleY = 1d + HoverScaleBoost;
            return;
        }

        chrome.Background = isHovering ? HoverBrush : FillBrush;
        icon.Foreground = IconBrush;
        var s = 1d + (isHovering ? HoverScaleBoost : 0d);
        hoverScale.ScaleX = s;
        hoverScale.ScaleY = s;
    }
}
