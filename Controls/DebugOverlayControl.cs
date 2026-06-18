using System;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;

namespace UniversalUmap.Rendering.Controls;

public sealed class DebugOverlayControl : Control
{
    private static readonly IBrush OverlayTextBrush = Brushes.White;
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch fpsSampleTimer = Stopwatch.StartNew();
    private bool showDebugOverlay = true;
    private ulong frameCount;
    private ulong lastFpsFrameCount;
    private float fps;
    private Typeface? overlayTypeface;
    private FormattedText? overlayText;
    private string? lastText;
    private bool overlayDirty = true;

    public static readonly StyledProperty<VulkanViewerControl?> SourceProperty =
        AvaloniaProperty.Register<DebugOverlayControl, VulkanViewerControl?>(nameof(Source));

    public VulkanViewerControl? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public DebugOverlayControl()
    {
        IsHitTestVisible = false;

        refreshTimer.Tick += (_, _) =>
        {
            UpdateFps();
            overlayDirty = true;
            RefreshOverlayText();
            if (showDebugOverlay)
                InvalidateVisual();
        };
        refreshTimer.Start();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (overlayText is null)
            return new Size(0, 0);

        return new Size(overlayText.WidthIncludingTrailingWhitespace, overlayText.Height);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Source is null || !showDebugOverlay || overlayText is null)
            return;

        context.DrawText(overlayText, new Point(0, 0));
    }

    private void SubscribeToSource(VulkanViewerControl source)
    {
        source.FrameRendered += OnFrameRendered;
        source.DebugOverlayToggleRequested += OnDebugOverlayToggleRequested;
    }

    private void UnsubscribeFromSource(VulkanViewerControl source)
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
        RefreshOverlayText();
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

    private void RefreshOverlayText()
    {
        if (!overlayDirty)
            return;

        if (!showDebugOverlay || Source is null)
        {
            overlayText = null;
            lastText = null;
            overlayDirty = false;
            return;
        }

        var typeface = overlayTypeface ??= new Typeface(
            TextElement.GetFontFamily(this),
            TextElement.GetFontStyle(this),
            TextElement.GetFontWeight(this));

        var ms = fps > 0f ? 1000f / fps : 0f;
        var text = $"FPS: {fps:0.0}\n{ms:0.0} ms";

        if (text == lastText && overlayText is not null)
        {
            overlayDirty = false;
            return;
        }

        overlayText = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            12d,
            OverlayTextBrush);

        lastText = text;
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

        if (change.OldValue is VulkanViewerControl oldSource)
            UnsubscribeFromSource(oldSource);
        if (change.NewValue is VulkanViewerControl newSource)
            SubscribeToSource(newSource);

        showDebugOverlay = true;
        fps = 0f;
        frameCount = 0;
        lastFpsFrameCount = 0;
        fpsSampleTimer.Restart();
        overlayDirty = true;
        RefreshOverlayText();
        InvalidateVisual();
    }
}
