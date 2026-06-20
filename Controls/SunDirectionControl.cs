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
using UniversalUmap.Rendering.Scenes;
using UniversalUmap.Rendering.Vulkan;

namespace UniversalUmap.Rendering.Controls;

public sealed class SunDirectionControl : VulkanShaderControl
{
    private const double RotateGizmoSize = 134d;
    private const double CornerInset = 15d;
    private const double SunWidgetScale = 0.72d;
    private const double SunWidgetSize = (RotateGizmoSize * 0.5d) * SunWidgetScale;
    private static readonly Vector3 WorldUp = new(0f, -1f, 0f);
    private static readonly Vector3 LocalRight = Vector3.UnitX;
    private static readonly Vector3 LocalSunDirection = Vector3.UnitY;

    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants
    {
        public Vector4 LightDir;
    }

    private VulkanRasterShaderProgram? shaderProgram;
    private Context? shaderContext;
    private VulkanViewerControl? subscribedSource;

    private Vector3 direction;
    private Quaternion directionRotation = Quaternion.Identity;
    private bool hasDirectionFromSource;
    private bool suppressUiEvents;

    public static readonly StyledProperty<VulkanViewerControl?> SourceProperty =
        AvaloniaProperty.Register<SunDirectionControl, VulkanViewerControl?>(nameof(Source));

    public VulkanViewerControl? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public SunDirectionControl()
    {
        IsHitTestVisible = true;
        Focusable = true;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, CornerInset, CornerInset);
        Width = SunWidgetSize;
        Height = SunWidgetSize;

        AddHandler(PointerPressedEvent, OnPointerPressedRouted, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPointerMovedRouted, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleasedRouted, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnPointerCaptureLostRouted, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnsubscribeFromSource(subscribedSource);
        if (PointerCapture.IsOwnedBy(this))
            PointerCapture.End(this);
        hasDirectionFromSource = false;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnGpuResourcesInvalidated()
    {
        shaderProgram?.Dispose();
        shaderProgram = null;
        shaderContext = null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SubscribeToSource(Source);
        RefreshFromSource();
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

    protected override Size MeasureOverride(Size availableSize) => new(SunWidgetSize, SunWidgetSize);

    protected override void DrawHitTestBackground(DrawingContext context, Rect bounds)
    {
        var radius = Math.Min(bounds.Width, bounds.Height) * 0.5;
        var center = bounds.Center;
        context.DrawEllipse(HitTestBrush, null, center, radius, radius);
    }

    protected override void OnRasterDraw(Context context, VulkanImage target)
    {
        if (!hasDirectionFromSource)
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
            var push = new PushConstants
            {
                LightDir = new Vector4(direction, 0f)
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

        var normalizedDirection = NormalizeOrDefault(sourceScene.Environment.DirectionalDirection);
        suppressUiEvents = true;
        direction = normalizedDirection;
        directionRotation = BuildRotationFromDirection(direction);
        hasDirectionFromSource = true;
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

        var p = local;
        var inDisk = IsPointInsideDisk(p);
        Log.Information(
            "SunDirectionWidget pointer pressed. Source={SourceType} Handled={Handled} InBounds={InBounds} InDisk={InDisk} Pos={Pos}",
            e.Source?.GetType().Name ?? "<null>",
            e.Handled,
            withinBounds,
            inDisk,
            p);

        if (!inDisk)
            return;

        var began = PointerCapture.TryBegin(this, e.Pointer, p);
        Log.Information("SunDirectionWidget capture begin result={Began} Pos={Pos}", began, p);
        if (began)
            e.Handled = true;
    }

    private void OnPointerMovedRouted(object? sender, PointerEventArgs e)
    {
        if (!PointerCapture.IsOwnedBy(this))
            return;

        var p = e.GetPosition(this);
        var delta = PointerCapture.UpdateMove(this, p);
        var yaw = (float)(-delta.X * 0.01);
        var pitch = (float)(-delta.Y * 0.01);
        if (Math.Abs(yaw) > float.Epsilon || Math.Abs(pitch) > float.Epsilon)
        {
            directionRotation = ApplyOrbitDelta(directionRotation, yaw, pitch);
            direction = NormalizeOrDefault(Vector3.Transform(LocalSunDirection, directionRotation));
            Log.Information("SunDirectionWidget rotate orbit direction={Direction}", direction);
            InvalidateGpuFrame();

            if (!suppressUiEvents && Source is { } source)
            {
                suppressUiEvents = true;
                try
                {
                    source.SetDirectionalLightDirection(direction);
                }
                finally
                {
                    suppressUiEvents = false;
                }
            }
        }

        e.Handled = true;
    }

    private void OnPointerReleasedRouted(object? sender, PointerReleasedEventArgs e)
    {
        if (!PointerCapture.IsOwnedBy(this))
            return;

        Log.Information("SunDirectionWidget capture end (release).");
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
        Log.Information("SunDirectionWidget pointer capture lost.");
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

    private static Quaternion ApplyOrbitDelta(Quaternion currentRotation, float yaw, float pitch)
    {
        var yawQ = Quaternion.CreateFromAxisAngle(Vector3.Normalize(WorldUp), yaw);
        var right = Vector3.Transform(LocalRight, currentRotation);
        if (right.LengthSquared() < 0.000001f)
            right = LocalRight;
        else
            right = Vector3.Normalize(right);

        var pitchQ = Quaternion.CreateFromAxisAngle(right, pitch);
        return Quaternion.Normalize(yawQ * pitchQ * currentRotation);
    }

    private void EnsureProgram(Context context)
    {
        if (shaderProgram is not null && ReferenceEquals(shaderContext, context))
            return;

        shaderProgram?.Dispose();
        shaderContext = context;
        Log.Debug("Creating SunDirectionControl shader program.");
        shaderProgram = new VulkanRasterShaderProgram(
            context,
            "Assets/Shaders/Widgets/FullScreenTriVS.spv",
            "Assets/Shaders/Widgets/SunDirectionWidgetFS.spv",
            PrimitiveTopology.TriangleList,
            ShaderStageFlags.FragmentBit,
            (uint)Marshal.SizeOf<PushConstants>(),
            enableAlphaBlending: true);
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

        if (suppressUiEvents)
            return;

        var normalizedDirection = NormalizeOrDefault(settings.DirectionalDirection);
        suppressUiEvents = true;
        direction = normalizedDirection;
        directionRotation = BuildRotationFromDirection(direction);
        hasDirectionFromSource = true;
        suppressUiEvents = false;
        InvalidateGpuFrame();
    }

    private static Vector3 NormalizeOrDefault(Vector3 v)
    {
        if (v.LengthSquared() <= 0.000001f)
            return LocalSunDirection;

        return Vector3.Normalize(v);
    }

    private static Quaternion BuildRotationFromDirection(Vector3 direction)
    {
        var from = Vector3.Normalize(LocalSunDirection);
        var to = NormalizeOrDefault(direction);
        var dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);

        if (dot > 0.9999f)
            return Quaternion.Identity;

        if (dot < -0.9999f)
        {
            var axis = Vector3.Cross(from, Vector3.UnitX);
            if (axis.LengthSquared() < 0.000001f)
                axis = Vector3.Cross(from, Vector3.UnitZ);

            axis = Vector3.Normalize(axis);
            return Quaternion.CreateFromAxisAngle(axis, MathF.PI);
        }

        var rotationAxis = Vector3.Normalize(Vector3.Cross(from, to));
        var angle = MathF.Acos(dot);
        return Quaternion.CreateFromAxisAngle(rotationAxis, angle);
    }
}
