using System;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace UniversalUmap.Rendering.Controls;

public sealed class DebugOverlay : Control
{
    private static readonly IBrush OverlayBackgroundBrush = new SolidColorBrush(Color.FromArgb(155, 12, 12, 12));
    private static readonly IBrush OverlayTextBrush = Brushes.White;
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch fpsSampleTimer = Stopwatch.StartNew();
    private bool showDebugOverlay = true;
    private ulong frameCount;
    private ulong lastFpsFrameCount;
    private float fps;
    private Typeface? overlayTypeface;
    private FormattedText[] overlayLines = Array.Empty<FormattedText>();
    private Rect overlayPanelRect;
    private bool overlayDirty = true;

    public static readonly StyledProperty<VulkanViewer?> SourceProperty =
        AvaloniaProperty.Register<DebugOverlay, VulkanViewer?>(nameof(Source));

    public VulkanViewer? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public DebugOverlay()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        refreshTimer.Tick += (_, _) =>
        {
            UpdateFps();
            overlayDirty = true;
            RefreshOverlayTextCache();
            if (showDebugOverlay)
                InvalidateVisual();
        };
        refreshTimer.Start();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var source = Source;
        if (source is null || !showDebugOverlay)
            return;
        if (overlayLines.Length == 0)
            return;

        const double padding = 8d;
        context.DrawRectangle(OverlayBackgroundBrush, null, overlayPanelRect, 10, 10);

        var y = overlayPanelRect.Y + padding;
        for (var i = 0; i < overlayLines.Length; i++)
        {
            var line = overlayLines[i];
            context.DrawText(line, new Point(overlayPanelRect.X + padding, y));
            y += line.Height;
            if (i + 1 < overlayLines.Length)
                y += 2d;
        }
    }

    private void SubscribeToSource(VulkanViewer source)
    {
        source.FrameRendered += OnFrameRendered;
        source.DebugOverlayToggleRequested += OnDebugOverlayToggleRequested;
    }

    private void UnsubscribeFromSource(VulkanViewer source)
    {
        source.FrameRendered -= OnFrameRendered;
        source.DebugOverlayToggleRequested -= OnDebugOverlayToggleRequested;
    }

    private void OnFrameRendered()
    {
        frameCount++;
    }

    private void OnDebugOverlayToggleRequested()
    {
        showDebugOverlay = !showDebugOverlay;
        overlayDirty = true;
        RefreshOverlayTextCache();
        InvalidateVisual();
    }

    private void UpdateFps()
    {
        var elapsedSeconds = fpsSampleTimer.Elapsed.TotalSeconds;
        if (elapsedSeconds < 0.20d)
            return;

        var frameDelta = frameCount - lastFpsFrameCount;
        fps = elapsedSeconds > 0d ? (float)(frameDelta / elapsedSeconds) : 0f;
        lastFpsFrameCount = frameCount;
        fpsSampleTimer.Restart();
        overlayDirty = true;
    }

    private void RefreshOverlayTextCache()
    {
        if (!overlayDirty && overlayLines.Length > 0)
            return;

        if (!showDebugOverlay || Source is null)
        {
            overlayLines = Array.Empty<FormattedText>();
            overlayPanelRect = default;
            overlayDirty = false;
            return;
        }

        var typeface = overlayTypeface ??= new Typeface(
            TextElement.GetFontFamily(this),
            TextElement.GetFontStyle(this),
            TextElement.GetFontWeight(this));

        var source = Source;
        var pivot = source.ArcballPivotDebug;
        var camera = source.CameraPositionDebug;
        var lines = new[]
        {
            //$"FPS: {fps:0.0}",
            $"Selected: {source.SelectedInstanceName}",
            $"Arcball Pivot: ({pivot.X:0.0}, {pivot.Y:0.0}, {pivot.Z:0.0})",
            $"Camera: ({camera.X:0.0}, {camera.Y:0.0}, {camera.Z:0.0})"
        };

        const double fontSize = 12d;
        const double lineSpacing = 2d;
        const double padding = 8d;
        var newLines = new FormattedText[lines.Length];
        var maxWidth = 0d;
        var totalHeight = 0d;

        for (var i = 0; i < lines.Length; i++)
        {
            var text = new FormattedText(
                lines[i],
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                OverlayTextBrush);
            newLines[i] = text;
            maxWidth = Math.Max(maxWidth, text.WidthIncludingTrailingWhitespace);
            totalHeight += text.Height;
            if (i + 1 < lines.Length)
                totalHeight += lineSpacing;
        }

        overlayLines = newLines;
        overlayPanelRect = new Rect(12, 12, maxWidth + padding * 2, totalHeight + padding * 2);
        overlayDirty = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextElement.FontFamilyProperty ||
            change.Property == TextElement.FontStyleProperty ||
            change.Property == TextElement.FontWeightProperty)
        {
            overlayTypeface = null;
            overlayDirty = true;
        }

        if (change.Property != SourceProperty)
            return;

        if (change.OldValue is VulkanViewer oldSource)
            UnsubscribeFromSource(oldSource);
        if (change.NewValue is VulkanViewer newSource)
            SubscribeToSource(newSource);

        showDebugOverlay = true;
        fps = 0f;
        frameCount = 0;
        lastFpsFrameCount = 0;
        fpsSampleTimer.Restart();
        overlayDirty = true;
        RefreshOverlayTextCache();
        InvalidateVisual();
    }
}
