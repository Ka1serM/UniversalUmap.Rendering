using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Serilog;
using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Core;
using UniversalUmap.Rendering.Raytracing;
using UniversalUmap.Rendering.Rasterizing;
using UniversalUmap.Rendering.Scenes;
using UniversalUmap.Rendering.Vulkan;
using AvaloniaCompositor = Avalonia.Rendering.Composition.Compositor;
using VulkanCompositor = UniversalUmap.Rendering.Vulkan.Compositor;

namespace UniversalUmap.Rendering.Controls;

public sealed class VulkanViewerControl : CapturingControlBase, IDisposable
{
    private static readonly IBrush HitTestBrush = Brushes.Transparent;

    private CompositionSurfaceVisual? visual;
    private Context? context;
    private VulkanSurface? surface;

    private bool updateQueued;
    private bool initialized;
    private bool running;
    private long lifecycleVersion;
    private readonly Action update;
    private Task pendingDisposeTask = Task.CompletedTask;
    private Size lastLayoutSize;
    private double lastLayoutScaling = -1d;

    private readonly Input input;
    private Scene? scene;
    private GpuRaytracer? raytracer;
    private GpuRasterizer? rasterizer;
    private GpuScenePicker? picker;
    private VulkanCompositor? vulkanCompositor;
    private AvaloniaCompositor? avaloniaCompositor;
    private readonly Stopwatch renderTimer = Stopwatch.StartNew();
    private long lastRenderTicks;
    private int lastCameraMovingState = -1;
    private int lastShaderPixelSizePercent = 100;

    public event Action? RestartRequired;
    internal event Action? FrameRendered;
    internal event Action? DebugOverlayToggleRequested;
    public event Action<EnvironmentSettings>? EnvironmentSettingsChanged;

    internal string SelectedInstanceName => scene?.SelectedInstance?.Name ?? "<None>";
    internal Vector3 CameraPositionDebug => scene?.GetCameraViewSnapshot().Position ?? Vector3.Zero;
    internal Vector3 ArcballPivotDebug => scene?.GetCameraViewSnapshot().ArcballPivot ?? Vector3.Zero;
    protected override bool UseTimedHoldCapture => true;

    public Scene? Scene => scene;
    public Camera? Camera => scene?.Camera;
    public Context? VulkanContext => context;

    public RenderMode RenderMode
    {
        get => scene?.RenderSettings.RenderMode ?? RenderMode.AmbientOcclusion;
        set
        {
            if (scene is null || scene.RenderSettings.RenderMode == value)
                return;

            Log.Information("Viewer render mode set to {RenderMode}.", value);
            scene.SetRenderMode(value);
            QueueNextFrame();
        }
    }

    public VulkanViewerControl()
    {
        input = new Input();
        update = UpdateFrame;
        Focusable = true;
        IsTabStop = true;
    }

    public void Dispose()
    {
        PauseControl();
        DestroyRenderResources();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        StartControl();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        PauseControl();
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(HitTestBrush, new Rect(Bounds.Size));
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        input.SetModifierState(e.KeyModifiers);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        input.OnFocusLost();
    }

    protected override void OnHoldPressStarted(PointerPressedEventArgs e, Point position, bool leftPressed, bool rightPressed)
    {
        input.SetModifierState(e.KeyModifiers);
        input.OnPointerMoved(position);
        Focus(NavigationMethod.Pointer);
    }

    protected override void OnHoldPointerMoved(PointerEventArgs e, Point position, bool leftPressed, bool rightPressed)
    {
        input.SetModifierState(e.KeyModifiers);
        input.OnPointerMoved(position);
    }

    protected override void OnHoldCaptureDelta(PointerEventArgs e, Point position, Avalonia.Vector delta)
    {
        input.SetModifierState(e.KeyModifiers);
        if (delta.X == 0d && delta.Y == 0d)
            return;

        input.OnPointerDelta(new System.Numerics.Vector2((float)delta.X, (float)delta.Y));
    }

    protected override void OnHoldCaptureButtonPressed(Point position, bool leftButton, bool rightButton)
    {
        input.OnPointerPressed(position, leftButton, rightButton);
    }

    protected override void OnHoldCaptureButtonReleased(bool leftButton, bool rightButton)
    {
        input.OnPointerReleased(leftButton, rightButton);
    }

    protected override void OnHoldQuickClick(PointerReleasedEventArgs e, Point releasePosition)
    {
        input.SetModifierState(e.KeyModifiers);

        if (PressStartedWithOnlyLeft &&
            e.InitialPressMouseButton == MouseButton.Left &&
            Bounds.Contains(releasePosition) &&
            !CaptureStartedDuringPress)
            HandleSelectionClick(releasePosition);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        input.SetModifierState(e.KeyModifiers);
        input.OnPointerWheel((float)e.Delta.Y);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F3)
        {
            DebugOverlayToggleRequested?.Invoke();
            e.Handled = true;
            return;
        }

        input.OnKeyDown(e.Key);
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        input.OnKeyUp(e.Key);
        e.Handled = true;
    }

    internal bool TryPickInstance(
        Point localPosition,
        Size viewportSize,
        out MeshInstance? instance,
        out uint instanceId,
        out int pixelX,
        out int pixelY)
    {
        instance = null;
        instanceId = ShaderDefines.INVALID_INSTANCE;
        pixelX = 0;
        pixelY = 0;

        return picker is not null && picker.TryPickInstance(localPosition, viewportSize, out instance, out instanceId, out pixelX, out pixelY);
    }

    private void StartControl()
    {
        if (running)
            return;

        running = true;
        lifecycleVersion++;
        LayoutUpdated += OnLayoutUpdated;
        lastLayoutSize = default;
        lastLayoutScaling = -1d;
        if (initialized)
        {
            QueueNextFrame();
            return;
        }

        _ = InitializeAsync(lifecycleVersion);
    }

    private void PauseControl()
    {
        if (!running)
            return;

        running = false;
        lifecycleVersion++;
        LayoutUpdated -= OnLayoutUpdated;

        ResetTimedHoldCapture();
        EndCapture();
        input.OnFocusLost();
        updateQueued = false;
    }

    private async Task InitializeAsync(long version)
    {
        try
        {
            await pendingDisposeTask;
            if (!IsCurrentLifecycle(version))
                return;

            var selfVisual = ElementComposition.GetElementVisual(this)!;
            avaloniaCompositor = selfVisual.Compositor;
            var drawingSurface = avaloniaCompositor.CreateDrawingSurface();
            visual = avaloniaCompositor.CreateSurfaceVisual();
            visual.Size = new(Bounds.Width, Bounds.Height);
            visual.Surface = drawingSurface;
            ElementComposition.SetElementChildVisual(this, visual);

            var interop = await avaloniaCompositor.TryGetCompositionGpuInterop();
            if (!IsCurrentLifecycle(version))
                return;
            if (interop is null)
            {
                ClearCompositionVisual();
                initialized = false;
                return;
            }

            context = await Context.AcquireAsync(avaloniaCompositor);
            if (!IsCurrentLifecycle(version))
                return;
            if (context is null)
            {
                ClearCompositionVisual();
                initialized = false;
                return;
            }

            surface = new VulkanSurface(context, interop, drawingSurface);
            BuildRenderResourcesIfNeeded();
            initialized = true;
            RaiseEnvironmentSettingsChanged();
            QueueNextFrame();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize VulkanViewer.");
            ClearCompositionVisual();
            initialized = false;
        }
    }

    private void BuildRenderResourcesIfNeeded()
    {
        if (context is null || scene is not null)
            return;

        scene = new Scene(context, input);
        scene.EnvironmentChanged += OnSceneEnvironmentChanged;
        scene.TryLoadDefaultEnvironment();
        raytracer = GpuRaytracer.Create(context, scene);
        picker = new GpuScenePicker(scene, GetActiveRenderPath());
        vulkanCompositor = new VulkanCompositor(context);

        try
        {
            rasterizer = new GpuRasterizer(context, scene);
        }
        catch (Exception ex)
        {
            rasterizer = null;
            Log.Error(ex, "Failed to create rasterizer render path. Falling back to raytracer-only viewer.");
        }
    }

    private void EnsureRaytracerBackendMatchesMode()
    {
        if (context is null || scene is null || raytracer is null)
            return;

        var shouldUseHardware = GpuRaytracer.PreferHardwareBackend(context, scene.RenderSettings.RenderMode);
        var hasHardwareBackend = raytracer is RtxRaytracer;
        if (shouldUseHardware == hasHardwareBackend)
            return;

        Log.Information(
            "Recreating raytracer for render mode {RenderMode}: backend {PreviousBackend} -> {NextBackend}.",
            scene.RenderSettings.RenderMode,
            raytracer.GetType().Name,
            shouldUseHardware ? nameof(RtxRaytracer) : nameof(ComputeRaytracer));

        raytracer.Dispose();
        raytracer = GpuRaytracer.Create(context, scene);
        if (picker is not null)
            picker.RenderPath = GetActiveRenderPath();
        scene.SetDirty(SceneDirtyFlags.Accumulation);
    }

    private async void ReinitializeAfterDeviceLoss()
    {
        var version = lifecycleVersion;
        initialized = false;
        updateQueued = false;
        ResetTimedHoldCapture();
        EndCapture();
        input.OnFocusLost();
        RestartRequired?.Invoke();

        FreeSurfaceResources();
        ClearCompositionVisual();
        await pendingDisposeTask;
        if (!IsCurrentLifecycle(version))
            return;
        _ = InitializeAsync(version);
    }

    private void DestroyRenderResources()
    {
        if (scene is not null)
        {
            scene.Synchronize(() =>
            {
                scene.EnvironmentChanged -= OnSceneEnvironmentChanged;
                vulkanCompositor?.Dispose();
                rasterizer?.Dispose();
                raytracer?.Dispose();
                scene.Dispose();
            });
        }

        vulkanCompositor = null;
        rasterizer = null;
        raytracer = null;
        picker = null;
        scene = null;
    }

    private void FreeSurfaceResources()
    {
        if (surface is not null)
        {
            var toDispose = surface;
            surface = null;
            var previousDisposeTask = pendingDisposeTask;
            pendingDisposeTask = DisposeSurfaceChainAsync(previousDisposeTask, toDispose);
        }

        context = null;
    }

    private void ClearCompositionVisual()
    {
        if (visual is not null)
            ElementComposition.SetElementChildVisual(this, null);

        visual = null;
        avaloniaCompositor = null;
    }

    private static async Task DisposeSurfaceChainAsync(Task previousDisposeTask, VulkanSurface activeSurface)
    {
        try
        {
            await previousDisposeTask;
            await activeSurface.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed disposing Vulkan surface resources.");
        }
    }

    private void UpdateFrame()
    {
        updateQueued = false;
        if (!running)
            return;

        var root = this.GetVisualRoot();
        if (root is null || visual is null || surface is null || context is null || scene is null || raytracer is null || avaloniaCompositor is null)
            return;

        var surfacePixelSize = PixelSize.FromSize(Bounds.Size, root.RenderScaling);
        visual.Size = new(Bounds.Width, Bounds.Height);
        if (surfacePixelSize.Width <= 0 || surfacePixelSize.Height <= 0)
            return;

        try
        {
            if (surface.TryAcquireRenderLease(surfacePixelSize, out var lease))
            {
                try
                {
                    var commandBuffer = RenderFrame(lease.Image);
                    if (commandBuffer is null)
                        return;

                    if (lease.WaitForAvailability)
                    {
                        context.SubmitCommandBuffer(
                            commandBuffer,
                            [lease.ImageAvailableSemaphore],
                            [PipelineStageFlags.AllCommandsBit],
                            [lease.RenderFinishedSemaphore]);
                    }
                    else
                    {
                        context.SubmitCommandBuffer(
                            commandBuffer,
                            signalSemaphores: [lease.RenderFinishedSemaphore]);
                    }

                    surface.CompleteRender(lease, commandBuffer);
                    FrameRendered?.Invoke();
                }
                finally
                {
                    if (!surface.TryPresentLatestReadyFrame())
                        surface.TryScheduleReadyCallback(RequestUiFrame);
                }
            }
            else
            {
                surface.TryScheduleReadyCallback(RequestUiFrame);
            }
        }
        catch (VulkanException ex) when (ex.Result == Result.ErrorDeviceLost)
        {
            Log.Error(ex, "Vulkan device lost in VulkanViewer. Reinitializing control resources.");
            ReinitializeAfterDeviceLoss();
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Render frame failed in VulkanViewer; skipping frame.");
            QueueNextFrame();
            return;
        }

        QueueNextFrame();
    }

    private Context.CommandBuffer? RenderFrame(ImageResource target)
    {
        if (scene is null || context is null || raytracer is null || vulkanCompositor is null)
            return null;

        var nowTicks = renderTimer.ElapsedTicks;
        var rawDeltaSeconds = lastRenderTicks == 0 ? 1f / 60f : (float)(nowTicks - lastRenderTicks) / Stopwatch.Frequency;
        lastRenderTicks = nowTicks;
        var deltaSeconds = Math.Clamp(rawDeltaSeconds, 1f / 240f, 0.25f);
        Context.CommandBuffer? recordedCommandBuffer = null;

        scene.Synchronize(() =>
        {
            EnsureRaytracerBackendMatchesMode();
            var fullRenderPixelSize = GetRenderPixelSize(target.Size, RenderPixelSize.X1);
            var renderPath = GetActiveRenderPath();
            var useFullSizeRaytracerTargets = renderPath is GpuRaytracer;
            var requestedRenderPixelSize = GetRenderPixelSize(target.Size, scene.RenderSettings.PixelSize);

            PixelSize renderPixelSize;
            if (useFullSizeRaytracerTargets)
            {
                renderPixelSize = fullRenderPixelSize;
            }
            else if (scene.RenderSettings.ApplyPixelSizeOnlyWhileMoving)
            {
                renderPixelSize = fullRenderPixelSize;
            }
            else
            {
                renderPixelSize = requestedRenderPixelSize;
            }

            scene.UpdateCamera(renderPixelSize, deltaSeconds);
            var renderData = scene.CaptureRenderData();

            if (useFullSizeRaytracerTargets)
            {
                renderPath.ShaderPixelSizePercent = scene.RenderSettings.ApplyPixelSizeOnlyWhileMoving && renderData.IsMoving == 0
                    ? (int)RenderPixelSize.X1
                    : (int)scene.RenderSettings.PixelSize;
            }
            else
            {
                renderPath.ShaderPixelSizePercent = (int)RenderPixelSize.X1;
            }

            if (renderData.IsMoving != lastCameraMovingState || renderPath.ShaderPixelSizePercent != lastShaderPixelSizePercent)
            {
                scene.SetDirty(SceneDirtyFlags.Accumulation);
                lastCameraMovingState = renderData.IsMoving;
                lastShaderPixelSizePercent = renderPath.ShaderPixelSizePercent;
            }

            picker!.RenderPath = renderPath;

            var commandBuffer = context.CreateCommandBuffer();
            context.BeginCommandBuffer(commandBuffer);
            renderPath.Record(renderPixelSize, target, commandBuffer, renderData);
            vulkanCompositor.Record(
                commandBuffer,
                renderPath.OutputColor,
                renderPath.OutputAlbedo,
                renderPath.OutputNormal,
                renderPath.OutputCrypto,
                renderPath.OutputPosition,
                renderPath.OutputAdaptiveState,
                (int)renderData.BufferVisualization,
                renderData.RenderSettings.AdaptiveTargetError,
                renderData.RenderSettings.AdaptiveMinSamples,
                renderData.SelectedInstanceId,
                target,
                renderData.IsMoving,
                renderPath.PickBuffersFlippedY);
            target.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit);
            recordedCommandBuffer = commandBuffer;
        });

        return recordedCommandBuffer;
    }

    private void RequestUiFrame()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (running)
                QueueNextFrame();
        });
    }

    private IGpuRenderPath GetActiveRenderPath()
    {
        if (scene is null || raytracer is null)
            throw new InvalidOperationException("Render paths are not initialized.");

        if (scene.RenderSettings.RenderMode is RenderMode.Rasterized or RenderMode.RasterizedWithRayTracedShadows)
        {
            if (rasterizer is not null)
                return rasterizer;

            Log.Warning("Raster render mode requested, but rasterizer is unavailable. Falling back to raytracer path.");
        }

        return raytracer;
    }

    private void QueueNextFrame()
    {
        if (!running || !initialized || updateQueued || avaloniaCompositor is null || !IsVisible)
            return;

        updateQueued = true;
        avaloniaCompositor.RequestCompositionUpdate(update);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == BoundsProperty || change.Property == IsVisibleProperty)
        {
            if (change.Property == BoundsProperty)
                UpdateCameraRenderSizeFromLayout();

            QueueNextFrame();
        }

        base.OnPropertyChanged(change);
    }

    private void HandleSelectionClick(Point localPosition)
    {
        _ = TryPickInstance(localPosition, Bounds.Size, out _, out _, out _, out _);
    }

    private void RaiseEnvironmentSettingsChanged()
    {
        if (scene is not null)
            EnvironmentSettingsChanged?.Invoke(scene.Environment);
    }

    private void OnSceneEnvironmentChanged(EnvironmentSettings settings) => EnvironmentSettingsChanged?.Invoke(settings);

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        var root = this.GetVisualRoot();
        var scale = root?.RenderScaling ?? -1d;
        var size = Bounds.Size;
        if (size == lastLayoutSize && Math.Abs(scale - lastLayoutScaling) < 0.0001d)
            return;

        lastLayoutSize = size;
        lastLayoutScaling = scale;
        UpdateCameraRenderSizeFromLayout();
        QueueNextFrame();
    }

    private void UpdateCameraRenderSizeFromLayout()
    {
        // Avoid taking scene locks on UI/layout thread during interactive window resize.
        // Camera render size is updated each frame in RenderFrame() using the current render pixel size setting.
        var root = this.GetVisualRoot();
        if (root is null)
            return;

        var pixelSize = PixelSize.FromSize(Bounds.Size, root.RenderScaling);
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
            return;
    }

    private static PixelSize GetRenderPixelSize(PixelSize fullSize, RenderPixelSize pixelSize)
    {
        var divisor = Math.Max(1f, (int)pixelSize / 100f);
        var width = Math.Max(1, (int)Math.Ceiling(fullSize.Width / divisor));
        var height = Math.Max(1, (int)Math.Ceiling(fullSize.Height / divisor));
        return new PixelSize(width, height);
    }

    private bool IsCurrentLifecycle(long version) => running && lifecycleVersion == version;
}
