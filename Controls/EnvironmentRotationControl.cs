using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Serilog;
using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Core;
using UniversalUmap.Rendering.Scenes;
using UniversalUmap.Rendering.Vulkan;

namespace UniversalUmap.Rendering.Controls;

public sealed class EnvironmentRotationControl : VulkanShaderControl
{
    private const double CornerInset = 15d;
    private const double WidgetGap = 10d;
    private const double WidgetSize = 48d;
    private const double RightInset = CornerInset + WidgetSize + WidgetGap;

    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants
    {
        public Vector4 Params0;
    }

    private VulkanRasterShaderProgram? shaderProgram;
    private Context? shaderContext;
    private VulkanViewerControl? subscribedSource;
    private VulkanDescriptorSet? textureDescriptorSet;
    private TextureAsset? fallbackTexture;
    private int boundTextureIndex = int.MinValue;

    private bool suppressUiEvents;
    private bool hasRotationFromSource;
    private float rotationDegrees;
    private int environmentTextureIndex = -1;

    public static readonly StyledProperty<VulkanViewerControl?> SourceProperty =
        AvaloniaProperty.Register<EnvironmentRotationControl, VulkanViewerControl?>(nameof(Source));

    public VulkanViewerControl? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public EnvironmentRotationControl()
    {
        IsHitTestVisible = true;
        Focusable = true;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, RightInset, CornerInset);
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
        if (PointerCapture.IsOwnedBy(this))
            PointerCapture.End(this);
        hasRotationFromSource = false;
        base.OnDetachedFromVisualTree(e);
    }

    protected override unsafe void OnGpuResourcesInvalidated()
    {
        shaderProgram?.Dispose();
        shaderProgram = null;
        textureDescriptorSet?.Dispose();
        textureDescriptorSet = null;
        boundTextureIndex = int.MinValue;
        fallbackTexture?.Dispose();
        fallbackTexture = null;
        shaderContext = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            if (change.OldValue is VulkanViewerControl oldSource)
                UnsubscribeFromSource(oldSource);
            if (change.NewValue is VulkanViewerControl newSource)
                SubscribeToSource(newSource);
            RefreshFromSource();
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(WidgetSize, WidgetSize);

    protected override void DrawHitTestBackground(DrawingContext context, Rect bounds)
    {
        var radius = Math.Min(bounds.Width, bounds.Height) * 0.5;
        var center = bounds.Center;
        context.DrawEllipse(HitTestBrush, null, center, radius, radius);
    }

    protected override void OnRasterDraw(Context context, VulkanImage target)
    {
        if (!hasRotationFromSource)
            RefreshFromSource();

        EnsureProgram(context);
        if (shaderProgram is null)
            return;

        var source = Source;
        var sourceScene = source?.Scene;
        if (sourceScene is null)
            return;

        sourceScene.Synchronize(() =>
        {
            UpdateEnvironmentTextureBinding(sourceScene);
            var push = new PushConstants
            {
                Params0 = new Vector4(rotationDegrees, 0f, 0f, 0f)
            };
            shaderProgram.Draw(target, 3, null, 0, in push);
        });
    }

    private void RefreshFromSource()
    {
        var source = Source;
        if (source is null)
            return;

        var sourceScene = source.Scene;
        if (sourceScene is null)
            return;

        suppressUiEvents = true;
        rotationDegrees = sourceScene.Environment.Rotation;
        environmentTextureIndex = sourceScene.Environment.TextureIndex;
        hasRotationFromSource = true;
        suppressUiEvents = false;
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

        if (PointerCapture.TryBegin(this, e.Pointer, local))
            e.Handled = true;
    }

    private void OnPointerMovedRouted(object? sender, PointerEventArgs e)
    {
        if (!PointerCapture.IsOwnedBy(this))
            return;

        var p = e.GetPosition(this);
        var delta = PointerCapture.UpdateMove(this, p);
        if (Math.Abs(delta.X) > double.Epsilon)
        {
            rotationDegrees = WrapDegrees(rotationDegrees + (float)(delta.X * 0.35));
            InvalidateGpuFrame();
            if (!suppressUiEvents && Source is { } source)
            {
                var scene = source.Scene;
                if (scene is not null)
                {
                    scene.Synchronize(() => scene.Environment.Rotation = rotationDegrees);
                }
            }
        }

        e.Handled = true;
    }

    private void OnPointerReleasedRouted(object? sender, PointerReleasedEventArgs e)
    {
        if (!PointerCapture.IsOwnedBy(this))
            return;

        PointerCapture.End(this, e.Pointer);
        e.Handled = true;
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        if (PointerCapture.IsOwnedBy(this))
            PointerCapture.End(this);
    }

    private void OnPointerCaptureLostRouted(object? sender, PointerCaptureLostEventArgs e)
    {
        if (PointerCapture.IsOwnedBy(this))
            PointerCapture.End(this);
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

    private void EnsureProgram(Context context)
    {
        if (shaderProgram is not null && ReferenceEquals(shaderContext, context))
            return;

        shaderProgram?.Dispose();
        shaderContext = context;
        Log.Debug("Creating EnvironmentRotationControl shader program.");
        EnsureDescriptorResources(context);
        EnsureFallbackTexture(context);
        shaderProgram = new VulkanRasterShaderProgram(
            context,
            "Assets/Shaders/Widgets/FullScreenTriVS.spv",
            "Assets/Shaders/Widgets/EnvironmentRotationWidgetFS.spv",
            PrimitiveTopology.TriangleList,
            ShaderStageFlags.FragmentBit,
            (uint)Marshal.SizeOf<PushConstants>(),
            enableAlphaBlending: true,
            externalDescriptorSetLayout: textureDescriptorSet!.Layout,
            externalDescriptorSet: textureDescriptorSet.Set);
    }

    private void EnsureDescriptorResources(Context context)
    {
        if (textureDescriptorSet is not null)
            return;

        textureDescriptorSet = new VulkanDescriptorSetBuilder()
            .Add(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit)
            .Build(context);
    }

    private void UpdateEnvironmentTextureBinding(Scene scene)
    {
        if (shaderContext is null || textureDescriptorSet is null)
            return;

        if (boundTextureIndex == environmentTextureIndex)
            return;

        DescriptorImageInfo imageInfo;
        if (scene.TryGetTextureAt(environmentTextureIndex, out var texture) && texture is not null)
            imageInfo = texture.GetDescriptorImageInfo();
        else
            imageInfo = fallbackTexture?.GetDescriptorImageInfo() ?? default;

        new VulkanDescriptorWriter()
            .CombinedImageSampler(0, imageInfo)
            .Update(shaderContext, textureDescriptorSet.Set);
        boundTextureIndex = environmentTextureIndex;
    }

    private void EnsureFallbackTexture(Context context)
    {
        if (fallbackTexture is not null)
            return;

        fallbackTexture = TextureAsset.CreateRgba8(
            context,
            "EnvironmentWidgetFallback",
            string.Empty,
            new byte[] { 96, 132, 184, 255 },
            1,
            1);
    }

    private void SubscribeToSource(VulkanViewerControl? source)
    {
        if (ReferenceEquals(subscribedSource, source))
            return;

        UnsubscribeFromSource(subscribedSource);
        subscribedSource = source;
        if (source is null)
            return;

        source.EnvironmentSettingsChanged += OnEnvironmentSettingsChanged;
    }

    private void UnsubscribeFromSource(VulkanViewerControl? source)
    {
        if (source is null)
            return;

        source.EnvironmentSettingsChanged -= OnEnvironmentSettingsChanged;
        if (ReferenceEquals(subscribedSource, source))
            subscribedSource = null;
    }

    private void OnEnvironmentSettingsChanged(EnvironmentSettings settings)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnEnvironmentSettingsChanged(settings));
            return;
        }

        suppressUiEvents = true;
        rotationDegrees = settings.Rotation;
        environmentTextureIndex = settings.TextureIndex;
        hasRotationFromSource = true;
        suppressUiEvents = false;
        InvalidateGpuFrame();
    }
}
