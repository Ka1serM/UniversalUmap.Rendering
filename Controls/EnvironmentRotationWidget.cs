using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering.Controls;

public sealed class EnvironmentRotationWidget : VulkanShaderControl
{
    private const double WidgetSize = 67d;
    private const double RightInset = 15d + WidgetSize + 10d;
    private const double BottomInset = 15d;

    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants
    {
        public Vector4 Params0; // x = rotationDeg, y = bindless texture index
    }

    private readonly ControlCaptureApi capture;
    private VulkanRasterShaderProgram? shaderProgram;
    private Context? shaderContext;
    private VulkanViewer? subscribedSource;

    private bool suppressUiEvents;
    private bool hasRotationFromSource;
    private float rotationDegrees;
    private int environmentTextureIndex = -1;

    public static readonly StyledProperty<VulkanViewer?> SourceProperty =
        AvaloniaProperty.Register<EnvironmentRotationWidget, VulkanViewer?>(nameof(Source));

    public VulkanViewer? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public EnvironmentRotationWidget()
    {
        capture = new ControlCaptureApi(this);
        IsHitTestVisible = true;
        Focusable = true;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, RightInset, BottomInset);
        Width = WidgetSize;
        Height = WidgetSize;

        AddHandler(PointerPressedEvent, OnPointerPressedRouted, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPointerMovedRouted, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleasedRouted, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnPointerCaptureLostRouted, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SubscribeToSource(Source);
        RefreshFromSource();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnsubscribeFromSource(subscribedSource);
        capture.End();
        shaderProgram?.Dispose();
        shaderProgram = null;
        shaderContext = null;
        hasRotationFromSource = false;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            if (change.OldValue is VulkanViewer oldSource)
                UnsubscribeFromSource(oldSource);
            if (change.NewValue is VulkanViewer newSource)
                SubscribeToSource(newSource);
            RefreshFromSource();
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(WidgetSize, WidgetSize);

    protected override void OnRasterDraw(Renderer renderer, ImageResource target)
    {
        if (!hasRotationFromSource)
            RefreshFromSource();

        EnsureProgram(renderer);
        if (shaderProgram is null)
            return;

        lock (renderer.Scene.SyncRoot)
        {
            var push = new PushConstants
            {
                Params0 = new Vector4(rotationDegrees, environmentTextureIndex, 0f, 0f)
            };
            shaderProgram.Draw(target, 3, null, 0, in push);
        }
    }

    private void RefreshFromSource()
    {
        var source = Source;
        if (source is null || !source.TryGetEnvironmentSettings(out var settings))
            return;

        suppressUiEvents = true;
        var changed = !hasRotationFromSource ||
                      MathF.Abs(rotationDegrees - settings.Rotation) > 0.0001f ||
                      environmentTextureIndex != settings.TextureIndex;
        rotationDegrees = settings.Rotation;
        environmentTextureIndex = settings.TextureIndex;
        hasRotationFromSource = true;
        suppressUiEvents = false;
        if (changed)
            InvalidateGpuFrame();
    }

    private void OnPointerPressedRouted(object? sender, PointerPressedEventArgs e)
    {
        var local = e.GetPosition(this);
        var localRect = new Rect(Bounds.Size);
        var withinBounds = localRect.Contains(local);
        if (!ReferenceEquals(e.Source, this) && !withinBounds)
            return;

        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed)
            return;
        if (!IsPointInsideDisk(local))
            return;

        if (capture.Begin(e.Pointer, local))
            e.Handled = true;
    }

    private void OnPointerMovedRouted(object? sender, PointerEventArgs e)
    {
        if (!capture.IsActive)
            return;

        if (capture.TryConsumeWarpMove())
        {
            e.Handled = true;
            return;
        }

        var p = e.GetPosition(this);
        var delta = capture.GetDelta(p);
        if (Math.Abs(delta.X) > double.Epsilon)
        {
            rotationDegrees = WrapDegrees(rotationDegrees + (float)(delta.X * 0.35));
            InvalidateGpuFrame();
            if (!suppressUiEvents)
                Source?.SetEnvironmentRotation(rotationDegrees);
        }

        capture.TryWrap(p);
        e.Handled = true;
    }

    private void OnPointerReleasedRouted(object? sender, PointerReleasedEventArgs e)
    {
        if (!capture.IsActive)
            return;

        capture.End(e.Pointer);
        e.Handled = true;
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        capture.End();
    }

    private void OnPointerCaptureLostRouted(object? sender, PointerCaptureLostEventArgs e)
    {
        capture.End();
    }

    private bool IsPointInsideDisk(Point p)
    {
        var size = Bounds.Size;
        var radius = Math.Min(size.Width, size.Height) * 0.5;
        var center = new Point(size.Width * 0.5, size.Height * 0.5);
        if (radius <= 0)
            return false;

        var x = (float)((p.X - center.X) / radius);
        var y = (float)((p.Y - center.Y) / radius);
        return (x * x) + (y * y) <= 1f;
    }

    private static float WrapDegrees(float degrees)
    {
        var wrapped = degrees % 360f;
        if (wrapped > 180f)
            wrapped -= 360f;
        else if (wrapped < -180f)
            wrapped += 360f;
        return wrapped;
    }

    private void EnsureProgram(Renderer renderer)
    {
        var context = renderer.Context;
        if (shaderProgram is not null && ReferenceEquals(shaderContext, context))
            return;

        shaderProgram?.Dispose();
        shaderContext = context;
        if (!renderer.TryGetBindlessTextureDescriptors(out var bindlessSetLayout, out var bindlessSet))
        {
            shaderProgram = null;
            return;
        }
        shaderProgram = new VulkanRasterShaderProgram(
            context,
            "SunDirectionWidgetVS.spv",
            "EnvironmentRotationWidgetFS.spv",
            PrimitiveTopology.TriangleList,
            ShaderStageFlags.FragmentBit,
            (uint)Marshal.SizeOf<PushConstants>(),
            enableAlphaBlending: true,
            externalDescriptorSetLayout: bindlessSetLayout,
            externalDescriptorSet: bindlessSet);
    }

    private void SubscribeToSource(VulkanViewer? source)
    {
        if (ReferenceEquals(subscribedSource, source))
            return;

        UnsubscribeFromSource(subscribedSource);
        subscribedSource = source;
        if (source is null)
            return;

        source.EnvironmentSettingsChanged += OnEnvironmentSettingsChanged;
    }

    private void UnsubscribeFromSource(VulkanViewer? source)
    {
        if (source is null)
            return;

        source.EnvironmentSettingsChanged -= OnEnvironmentSettingsChanged;
        if (ReferenceEquals(subscribedSource, source))
            subscribedSource = null;
    }

    private void OnEnvironmentSettingsChanged(VulkanViewer.EnvironmentSettings settings)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnEnvironmentSettingsChanged(settings));
            return;
        }

        suppressUiEvents = true;
        var changed = !hasRotationFromSource ||
                      MathF.Abs(rotationDegrees - settings.Rotation) > 0.0001f ||
                      environmentTextureIndex != settings.TextureIndex;
        rotationDegrees = settings.Rotation;
        environmentTextureIndex = settings.TextureIndex;
        hasRotationFromSource = true;
        suppressUiEvents = false;
        if (changed)
            InvalidateGpuFrame();
    }
}
