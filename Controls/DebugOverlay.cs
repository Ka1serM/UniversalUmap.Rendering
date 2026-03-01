using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;

namespace UniversalUmap.Rendering.Controls;

public sealed class DebugOverlay : Control
{
    private static readonly IBrush OverlayBackgroundBrush = new SolidColorBrush(Color.FromArgb(155, 12, 12, 12));
    private static readonly IBrush OverlayTextBrush = Brushes.White;
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

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
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;

        refreshTimer.Tick += (_, _) => InvalidateVisual();
        refreshTimer.Start();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var source = Source;
        if (source is null || !source.ShowDebugOverlay)
            return;

        var pivot = source.ArcballPivotDebug;
        var camera = source.CameraPositionDebug;
        var lines = new[]
        {
            //$"FPS: {source.OverlayFps:0.0}",
            $"Selected: {source.SelectedInstanceName}",
            $"Arcball Pivot: ({pivot.X:0.0}, {pivot.Y:0.0}, {pivot.Z:0.0})",
            $"Camera: ({camera.X:0.0}, {camera.Y:0.0}, {camera.Z:0.0})"
        };

        const double fontSize = 12d;
        const double lineSpacing = 2d;
        const double padding = 8d;
        var overlayTypeface = new Typeface(
            TextElement.GetFontFamily(this),
            TextElement.GetFontStyle(this),
            TextElement.GetFontWeight(this));

        var maxWidth = 0d;
        var totalHeight = 0d;
        var formattedLines = new FormattedText[lines.Length];

        for (var i = 0; i < lines.Length; i++)
        {
            var text = new FormattedText(
                lines[i],
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                overlayTypeface,
                fontSize,
                OverlayTextBrush);
            formattedLines[i] = text;
            maxWidth = Math.Max(maxWidth, text.WidthIncludingTrailingWhitespace);
            totalHeight += text.Height;
            if (i + 1 < lines.Length)
                totalHeight += lineSpacing;
        }

        var panelRect = new Rect(12, 12, maxWidth + padding * 2, totalHeight + padding * 2);
        context.DrawRectangle(OverlayBackgroundBrush, null, panelRect, 10, 10);

        var y = panelRect.Y + padding;
        for (var i = 0; i < formattedLines.Length; i++)
        {
            var text = formattedLines[i];
            context.DrawText(text, new Point(panelRect.X + padding, y));
            y += text.Height + lineSpacing;
        }
    }
}
