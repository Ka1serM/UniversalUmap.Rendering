using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Serilog;
using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Core;
using UniversalUmap.Rendering.Rasterizing;
using UniversalUmap.Rendering.Raytracing;
using UniversalUmap.Rendering.Scenes;
using UniversalUmap.Rendering.Vulkan;
using AvaloniaCompositor = Avalonia.Rendering.Composition.Compositor;
using VulkanCompositor = UniversalUmap.Rendering.Vulkan.Compositor;

namespace UniversalUmap.Rendering.Controls;

public sealed class VulkanViewerControl : CapturingControlBase, IDisposable
{
    private static readonly IBrush HitTestBrush = Brushes.Transparent;

    // Composition
    private CompositionSurfaceVisual? visual;
    private AvaloniaCompositor? avaloniaCompositor;

    // Vulkan
    private Context? context;
    private VulkanSwapchain? swapchain;
    private OffscreenPresentationBuffer? presentationBuffer;
    private VulkanCompositor? vulkanCompositor;
    private GpuRaytracer? raytracer;
    private GpuRasterizer? rasterizer;

    // Scene
    private Scene? scene;
    private GpuScenePicker? picker;

    // Lifecycle — matching GpuInterop DrawingSurfaceDemoBase exactly
    private readonly Action update;
    private bool updateQueued;
    private bool initialized;
    private long lifecycleVersion;
    private Task pendingDisposeTask = Task.CompletedTask;

    // Rendering
    private readonly Input input;
    private readonly Stopwatch renderTimer = Stopwatch.StartNew();
    private long lastRenderTicks;

    public event Action? RestartRequired;
    public event Action<EnvironmentSettings>? EnvironmentSettingsChanged;
    internal event Action? FrameRendered;
    internal event Action? DebugOverlayToggleRequested;

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
            if (scene is null) return;
            var effective = ToSupportedRenderMode(value);
            if (scene.RenderSettings.RenderMode == effective) return;
            if (effective != value)
                Log.Warning("Render mode {Requested} unavailable; falling back to {Fallback}.", value, effective);
            Log.Information("Render mode set to {Mode}.", effective);
            scene.SetRenderMode(effective);
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
        lifecycleVersion++;
        ResetTimedHoldCapture();
        EndCapture();
        input.OnFocusLost();
        updateQueued = false;

        scene?.Synchronize(() =>
        {
            scene.EnvironmentChanged -= OnSceneEnvironmentChanged;
            vulkanCompositor?.Dispose();
            rasterizer?.Dispose();
            raytracer?.Dispose();
            scene.Dispose();
        });
        presentationBuffer?.Dispose();
        _ = swapchain?.DisposeAsync();
        vulkanCompositor = null;
        rasterizer = null;
        raytracer = null;
        picker = null;
        scene = null;
        presentationBuffer = null;
        swapchain = null;
    }

    // --- Lifecycle — matching GpuInterop DrawingSurfaceDemoBase ---

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Initialize();
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        if (initialized)
        {
            presentationBuffer?.Dispose();
            presentationBuffer = null;
            _ = swapchain?.DisposeAsync();
            swapchain = null;
            context = null;

            scene?.Synchronize(() =>
            {
                scene.EnvironmentChanged -= OnSceneEnvironmentChanged;
                vulkanCompositor?.Dispose();
                rasterizer?.Dispose();
                raytracer?.Dispose();
                scene.Dispose();
            });
            vulkanCompositor = null;
            rasterizer = null;
            raytracer = null;
            picker = null;
            scene = null;
        }

        initialized = false;
        base.OnDetachedFromLogicalTree(e);
    }

    async void Initialize()
    {
        try
        {
            await pendingDisposeTask;
            if (!IsCurrentLifecycle()) return;

            var selfVisual = ElementComposition.GetElementVisual(this)!;
            avaloniaCompositor = selfVisual.Compositor;
            var drawingSurface = avaloniaCompositor.CreateDrawingSurface();

            visual = avaloniaCompositor.CreateSurfaceVisual();
            visual.Size = new Avalonia.Vector(Bounds.Width, Bounds.Height);
            visual.Surface = drawingSurface;
            ElementComposition.SetElementChildVisual(this, visual);

            var interop = await avaloniaCompositor.TryGetCompositionGpuInterop();
            if (!IsCurrentLifecycle()) return;
            if (interop is null) { FailInit(); return; }

            context = await Context.AcquireAsync(avaloniaCompositor);
            if (!IsCurrentLifecycle()) return;
            if (context is null) { FailInit(); return; }

            swapchain = new VulkanSwapchain(context, interop, drawingSurface);

            scene = new Scene(context, input);
            scene.EnvironmentChanged += OnSceneEnvironmentChanged;
            scene.TryLoadDefaultEnvironment();
            scene.SetRenderMode(RenderMode.AmbientOcclusion);

            try
            {
                raytracer = GpuRaytracer.Create(context, scene);
                rasterizer = new GpuRasterizer(context, scene);
                vulkanCompositor = new VulkanCompositor(context);
                picker = new GpuScenePicker(scene, ResolveRenderPath(scene.RenderSettings.RenderMode));
                if (scene.RenderSettings.RenderMode != ToSupportedRenderMode(scene.RenderSettings.RenderMode))
                    scene.SetRenderMode(ToSupportedRenderMode(scene.RenderSettings.RenderMode));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to create Vulkan raytracing renderer.");
                vulkanCompositor?.Dispose(); vulkanCompositor = null;
                rasterizer?.Dispose(); rasterizer = null;
                raytracer?.Dispose(); raytracer = null;
                picker = null;
            }

            initialized = true;
            EnvironmentSettingsChanged?.Invoke(scene.Environment);
            QueueNextFrame();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize VulkanViewer.");
            FailInit();
        }

        void FailInit()
        {
            if (visual is not null) ElementComposition.SetElementChildVisual(this, null);
            visual = null;
            avaloniaCompositor = null;
            initialized = false;
        }
    }

    private bool IsCurrentLifecycle() => lifecycleVersion == 0 || true;

    // --- Rendering — matching GpuInterop DrawingSurfaceDemoBase ---

    void UpdateFrame()
    {
        updateQueued = false;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        visual!.Size = new Avalonia.Vector(Bounds.Width, Bounds.Height);
        var size = PixelSize.FromSize(Bounds.Size, topLevel.RenderScaling);
        if (size.Width <= 0 || size.Height <= 0)
            return;

        if (visual is null || swapchain is null || context is null ||
            scene is null || raytracer is null || rasterizer is null || vulkanCompositor is null)
            return;

        try
        {
            using (swapchain.BeginDraw(size, out var swapchainImage))
            {
                var cmd = RenderFrame(swapchainImage.Image);
                if (cmd is not null)
                {
                    context.SubmitCommandBuffer(cmd, signalSemaphores: [swapchainImage.SemaphorePair.RenderFinishedSemaphore]);
                    FrameRendered?.Invoke();
                }
            }
        }
        catch (VulkanException ex) when (ex.Result == Result.ErrorDeviceLost)
        {
            Log.Error(ex, "Vulkan device lost; reinitializing.");
            initialized = false;
            updateQueued = false;
            RestartRequired?.Invoke();
            presentationBuffer?.Dispose();
            presentationBuffer = null;
            if (swapchain is not null)
            {
                var toDispose = swapchain;
                swapchain = null;
                var prev = pendingDisposeTask;
                pendingDisposeTask = DisposeSwapchainAsync(prev, toDispose);
            }
            context = null;
            if (visual is not null) ElementComposition.SetElementChildVisual(this, null);
            visual = null;
            avaloniaCompositor = null;
            _ = pendingDisposeTask.ContinueWith(_ => Initialize());
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Render frame failed; skipping.");
        }

        QueueNextFrame();
    }

    private Context.CommandBuffer? RenderFrame(VulkanImage target)
    {
        if (scene is null || context is null || raytracer is null || rasterizer is null || vulkanCompositor is null)
            return null;

        var now = renderTimer.ElapsedTicks;
        var delta = Math.Clamp(
            lastRenderTicks == 0 ? 1f / 60f : (float)(now - lastRenderTicks) / Stopwatch.Frequency,
            1f / 240f, 0.25f);
        lastRenderTicks = now;

        Context.CommandBuffer? result = null;
        scene.Synchronize(() =>
        {
            scene.UpdateCamera(target.Size, delta);
            var rd = scene.CaptureRenderData();
            var pixelSize = scene.RenderSettings.ApplyPixelSizeOnlyWhileMoving && rd.IsMoving == 0
                ? RenderPixelSize.X1
                : scene.RenderSettings.PixelSize;

            presentationBuffer ??= new OffscreenPresentationBuffer(context);
            presentationBuffer.EnsureSize(target.Size);

            var cmd = context.CreateCommandBuffer();
            context.BeginCommandBuffer(cmd);
            var renderPath = ResolveRenderPath(scene.RenderSettings.RenderMode);
            var selectionRenderPath = ResolveSelectionRenderPath(scene.RenderSettings.RenderMode, renderPath);
            picker!.RenderPath = selectionRenderPath;
            renderPath.ShaderPixelSizePercent = (int)pixelSize;
            renderPath.Record(target.Size, presentationBuffer.ColorImage, cmd, rd);
            vulkanCompositor.Record(
                cmd,
                renderPath.OutputColor, renderPath.OutputAlbedo, renderPath.OutputNormal,
                selectionRenderPath.OutputCrypto, selectionRenderPath.OutputPosition, renderPath.OutputAdaptiveState,
                (int)rd.BufferVisualization,
                rd.RenderSettings.AdaptiveTargetError, rd.RenderSettings.AdaptiveMinSamples,
                rd.SelectedInstanceId, presentationBuffer.ColorImage,
                rd.IsMoving, selectionRenderPath.PickBuffersFlippedY);
            presentationBuffer.BlitToPresentedImage(cmd, target);
            result = cmd;
        });

        return result;
    }

    void QueueNextFrame()
    {
        if (initialized && !updateQueued && avaloniaCompositor != null)
        {
            updateQueued = true;
            avaloniaCompositor.RequestCompositionUpdate(update);
        }
    }

    // --- Input ---

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(HitTestBrush, new Rect(Bounds.Size));
    }

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); input.SetModifierState(e.KeyModifiers); }
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        input.OnFocusLost();
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) { base.OnPointerWheelChanged(e); input.SetModifierState(e.KeyModifiers); input.OnPointerWheel((float)e.Delta.Y); e.Handled = true; }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F3) { DebugOverlayToggleRequested?.Invoke(); e.Handled = true; return; }
        input.OnKeyDown(e.Key);
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); input.OnKeyUp(e.Key); e.Handled = true; }
    protected override void OnHoldPressStarted(PointerPressedEventArgs e, Point position, bool leftPressed, bool rightPressed) { input.SetModifierState(e.KeyModifiers); input.OnPointerMoved(position); Focus(NavigationMethod.Pointer); }
    protected override void OnHoldPointerMoved(PointerEventArgs e, Point position, bool leftPressed, bool rightPressed) { input.SetModifierState(e.KeyModifiers); input.OnPointerMoved(position); }
    protected override void OnHoldCaptureButtonPressed(Point position, bool leftButton, bool rightButton) => input.OnPointerPressed(position, leftButton, rightButton);
    protected override void OnHoldCaptureButtonReleased(bool leftButton, bool rightButton) => input.OnPointerReleased(leftButton, rightButton);

    protected override void OnHoldCaptureDelta(PointerEventArgs e, Point position, Avalonia.Vector delta)
    {
        input.SetModifierState(e.KeyModifiers);
        if (delta.X != 0d || delta.Y != 0d)
            input.OnPointerDelta(new Vector2((float)delta.X, (float)delta.Y));
    }

    protected override void OnHoldQuickClick(PointerReleasedEventArgs e, Point releasePosition)
    {
        input.SetModifierState(e.KeyModifiers);
        if (PressStartedWithOnlyLeft && e.InitialPressMouseButton == MouseButton.Left &&
            Bounds.Contains(releasePosition) && !CaptureStartedDuringPress)
            TryPickInstance(releasePosition, Bounds.Size, out _, out _, out _, out _);
    }

    internal bool TryPickInstance(Point localPosition, Size viewportSize, out MeshInstance? instance, out uint instanceId, out int pixelX, out int pixelY)
    {
        instance = null; instanceId = ShaderDefines.INVALID_INSTANCE; pixelX = 0; pixelY = 0;
        return picker is not null && picker.TryPickInstance(localPosition, viewportSize, out instance, out instanceId, out pixelX, out pixelY);
    }

    // --- Helpers ---

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == BoundsProperty)
            QueueNextFrame();
        base.OnPropertyChanged(change);
    }

    public void SetDirectionalLightDirection(Vector3 direction)
    {
        if (scene is null) return;
        var normalized = direction.LengthSquared() <= 0.000001f ? new Vector3(0f, 1f, 0f) : Vector3.Normalize(direction);
        scene.Synchronize(() => scene.Environment.DirectionalDirection = normalized);
        EnvironmentSettingsChanged?.Invoke(scene.Environment);
        QueueNextFrame();
    }

    private void OnSceneEnvironmentChanged(EnvironmentSettings settings) => EnvironmentSettingsChanged?.Invoke(settings);

    private IGpuRenderPath ResolveRenderPath(RenderMode mode) =>
        mode is RenderMode.Rasterized
            ? rasterizer ?? throw new InvalidOperationException("Raster renderer is not initialized.")
            : raytracer ?? throw new InvalidOperationException("Raytracing renderer is not initialized.");

    private IGpuRenderPath ResolveSelectionRenderPath(RenderMode mode, IGpuRenderPath activeRenderPath) =>
        mode is RenderMode.Rasterized
            ? rasterizer ?? throw new InvalidOperationException("Raster renderer is not initialized.")
            : activeRenderPath;

    private static RenderMode ToSupportedRenderMode(RenderMode mode) => mode;

    private static async Task DisposeSwapchainAsync(Task prev, VulkanSwapchain s)
    {
        try { await prev; await s.DisposeAsync(); }
        catch (Exception ex) { Log.Warning(ex, "Failed disposing Vulkan swapchain."); }
    }
}
