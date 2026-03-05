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
using Avalonia.VisualTree;
using Serilog;
using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering.Controls;

public sealed class VulkanViewerControl : Control, IDisposable
{
    public readonly record struct EnvironmentSettings(
        float Rotation,
        float VisibleExposure,
        float LightingExposure,
        bool Visible,
        int TextureIndex,
        Vector3 DirectionalDirection,
        float DirectionalIntensity);

    private static readonly IBrush HitTestBrush = Brushes.Transparent;

    private CompositionSurfaceVisual? visual;
    private Compositor? compositor;
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

    private Input? input;
    private Scene? scene;
    private GpuRaytracer? raytracer;
    private GpuScenePicker? picker;
    private OverlayCompositor? overlayCompositor;
    private readonly Stopwatch renderTimer = Stopwatch.StartNew();
    private long lastRenderTicks;
    private const double ClickMoveThreshold = 4d;
    private bool pointerPressActive;
    private bool pointerPressLeftOnly;
    private bool pointerPressMoved;
    private Point pointerPressPosition;

    private EnvironmentSettings environment;
    private bool hasEnvironmentState;

    internal event Action? FrameRendered;
    internal event Action? DebugOverlayToggleRequested;
    internal event Action<EnvironmentSettings>? EnvironmentSettingsChanged;

    internal string SelectedInstanceName => scene?.SelectedInstance?.Name ?? "<None>";
    internal Vector3 CameraPositionDebug => scene?.CameraController.Position ?? Vector3.Zero;
    internal Vector3 ArcballPivotDebug => scene?.CameraController.ArcballPivot ?? Vector3.Zero;

    public Scene? Scene => scene;
    public Context? VulkanContext => context;

    public EnvironmentSettings Environment
    {
        get => environment;
        set
        {
            environment = value;
            hasEnvironmentState = true;
            ApplyEnvironmentToScene(environment);
            RaiseEnvironmentSettingsChanged();
        }
    }

    public VulkanViewerControl()
    {
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
        input?.SetModifierState(e.KeyModifiers);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        if (SharedPointerCapture.IsOwnedBy(this))
            SharedPointerCapture.End(this);
        input?.OnFocusLost();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var props = e.GetCurrentPoint(this).Properties;
        var leftPressed = props.IsLeftButtonPressed;
        var rightPressed = props.IsRightButtonPressed;
        if (!leftPressed && !rightPressed)
            return;

        var position = e.GetPosition(this);
        pointerPressActive = true;
        pointerPressLeftOnly = leftPressed && !rightPressed;
        pointerPressMoved = false;
        pointerPressPosition = position;
        input?.SetModifierState(e.KeyModifiers);
        input?.OnPointerMoved(position);
        Focus(NavigationMethod.Pointer);

        if (SharedPointerCapture.TryBegin(this, e.Pointer, position))
            input?.OnPointerPressed(position, leftPressed, rightPressed);

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var position = e.GetPosition(this);
        if (pointerPressActive && !pointerPressMoved)
        {
            var dx = position.X - pointerPressPosition.X;
            var dy = position.Y - pointerPressPosition.Y;
            pointerPressMoved = (dx * dx) + (dy * dy) > (ClickMoveThreshold * ClickMoveThreshold);
        }
        if (SharedPointerCapture.IsOwnedBy(this) && SharedPointerCapture.TryConsumeWarpSuppressedMove(this))
        {
            e.Handled = true;
            return;
        }

        if (!SharedPointerCapture.IsOwnedBy(this))
        {
            input?.SetModifierState(e.KeyModifiers);
            input?.OnPointerMoved(position);
            return;
        }

        var props = e.GetCurrentPoint(this).Properties;
        var leftPressed = props.IsLeftButtonPressed;
        var rightPressed = props.IsRightButtonPressed;

        var delta = SharedPointerCapture.GetDelta(this, position);
        input?.SetModifierState(e.KeyModifiers);
        if (delta.X != 0d || delta.Y != 0d)
            input?.OnPointerDelta(new Vector2((float)delta.X, (float)delta.Y));

        if (leftPressed || rightPressed)
            SharedPointerCapture.TryWrapAround(this, position);
        else
            SharedPointerCapture.End(this, e.Pointer);

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        var releasePosition = e.GetPosition(this);
        var updateKind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        var isLeftRelease = e.InitialPressMouseButton == MouseButton.Left || updateKind == PointerUpdateKind.LeftButtonReleased;
        var props = e.GetCurrentPoint(this).Properties;
        var leftPressed = props.IsLeftButtonPressed;
        var rightPressed = props.IsRightButtonPressed;
        switch (e.InitialPressMouseButton)
        {
            case MouseButton.Left:
                leftPressed = false;
                break;
            case MouseButton.Right:
                rightPressed = false;
                break;
        }

        if (SharedPointerCapture.IsOwnedBy(this))
        {
            switch (e.InitialPressMouseButton)
            {
                case MouseButton.Left:
                    input?.OnPointerReleased(leftButton: true, rightButton: false);
                    break;
                case MouseButton.Right:
                    input?.OnPointerReleased(leftButton: false, rightButton: true);
                    break;
            }

            if (!leftPressed && !rightPressed)
                SharedPointerCapture.End(this, e.Pointer);
        }

        if (pointerPressActive &&
            pointerPressLeftOnly &&
            !pointerPressMoved &&
            isLeftRelease &&
            Bounds.Contains(releasePosition))
        {
            input?.SetModifierState(e.KeyModifiers);
            HandleSelectionClick(releasePosition);
        }

        pointerPressActive = false;
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        pointerPressActive = false;
        if (SharedPointerCapture.IsOwnedBy(this))
            SharedPointerCapture.End(this);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        input?.SetModifierState(e.KeyModifiers);
        input?.OnPointerWheel((float)e.Delta.Y);
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

        input?.OnKeyDown(e.Key);
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        input?.OnKeyUp(e.Key);
        e.Handled = true;
    }

    internal bool TryGetBindlessTextureDescriptors(out DescriptorSetLayout layout, out DescriptorSet set)
    {
        if (scene is null || raytracer is null)
        {
            layout = default;
            set = default;
            return false;
        }

        lock (scene.SyncRoot)
            return raytracer.TryGetBindlessTextureDescriptors(out layout, out set);
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
        instanceId = SharedShaderDefines.InvalidInstance;
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

        SharedPointerCapture.End(this);
        input?.OnFocusLost();
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
            compositor = selfVisual.Compositor;
            var drawingSurface = compositor.CreateDrawingSurface();
            visual = compositor.CreateSurfaceVisual();
            visual.Size = new(Bounds.Width, Bounds.Height);
            visual.Surface = drawingSurface;
            ElementComposition.SetElementChildVisual(this, visual);

            var interop = await compositor.TryGetCompositionGpuInterop();
            if (!IsCurrentLifecycle(version))
                return;
            if (interop is null)
            {
                ClearCompositionVisual();
                initialized = false;
                return;
            }

            context = await Context.AcquireAsync(compositor);
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
            if (hasEnvironmentState)
                ApplyEnvironmentToScene(environment);
            else
                SyncEnvironmentFromScene();
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

        GpuStructLayoutValidator.ValidateOrThrow();

        input = new Input();
        scene = new Scene(context, input);
        scene.TryLoadDefaultEnvironment();
        raytracer = GpuRaytracer.Create(context, scene);
        picker = new GpuScenePicker(scene, raytracer);
        overlayCompositor = new OverlayCompositor(context);
    }

    private async void ReinitializeAfterDeviceLoss()
    {
        var version = lifecycleVersion;
        initialized = false;
        updateQueued = false;
        SharedPointerCapture.End(this);
        input?.OnFocusLost();

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
            lock (scene.SyncRoot)
            {
                overlayCompositor?.Dispose();
                raytracer?.Dispose();
                scene.Dispose();
            }
        }

        overlayCompositor = null;
        raytracer = null;
        picker = null;
        scene = null;
        input = null;
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
        compositor = null;
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
        if (root is null || visual is null || surface is null || context is null || scene is null || raytracer is null || overlayCompositor is null)
            return;

        var pixelSize = PixelSize.FromSize(Bounds.Size, root.RenderScaling);
        visual.Size = new(Bounds.Width, Bounds.Height);
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
            return;

        try
        {
            if (!surface.TryBeginDraw(pixelSize, out var image))
            {
                QueueNextFrame();
                return;
            }

            try
            {
                RenderFrame(image);
            }
            finally
            {
                surface.Present();
            }

            FrameRendered?.Invoke();
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

    private void RenderFrame(ImageResource target)
    {
        if (scene is null || context is null || raytracer is null || overlayCompositor is null)
            return;

        var nowTicks = renderTimer.ElapsedTicks;
        var rawDeltaSeconds = lastRenderTicks == 0 ? 1f / 60f : (float)(nowTicks - lastRenderTicks) / Stopwatch.Frequency;
        lastRenderTicks = nowTicks;
        var deltaSeconds = Math.Clamp(rawDeltaSeconds, 1f / 240f, 0.25f);

        lock (scene.SyncRoot)
        {
            scene.UpdateCamera(target.Size, deltaSeconds);

            var commandBuffer = context.CreateCommandBuffer();
            commandBuffer.BeginRecording();
            raytracer.Record(target, commandBuffer);

            var selectedInstanceId = scene.SelectedInstanceIndex >= 0
                ? (uint)scene.SelectedInstanceIndex
                : SharedShaderDefines.InvalidInstance;
            overlayCompositor.Record(
                commandBuffer,
                raytracer.OutputColor,
                raytracer.OutputCrypto,
                raytracer.OutputPosition,
                selectedInstanceId,
                target);
            commandBuffer.Submit();
        }
    }

    private void QueueNextFrame()
    {
        if (!running || !initialized || updateQueued || compositor is null || !IsVisible)
            return;

        updateQueued = true;
        compositor.RequestCompositionUpdate(update);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == BoundsProperty || change.Property == IsVisibleProperty)
            QueueNextFrame();

        base.OnPropertyChanged(change);
    }

    private void HandleSelectionClick(Point localPosition)
    {
        _ = TryPickInstance(localPosition, Bounds.Size, out _, out _, out _, out _);
    }

    private void ApplyEnvironmentToScene(EnvironmentSettings settings)
    {
        if (scene is null)
            return;

        scene.Mutate(current =>
        {
            var env = current.Environment;
            env.Rotation = settings.Rotation;
            env.VisibleExposure = settings.VisibleExposure;
            env.LightingExposure = settings.LightingExposure;
            env.Visible = settings.Visible ? 1 : 0;
            env.TextureIndex = settings.TextureIndex;
            env.DirectionalDirection = settings.DirectionalDirection;
            env.DirectionalIntensity = settings.DirectionalIntensity;
            current.SetEnvironmentData(env);
            return true;
        });
    }

    private void SyncEnvironmentFromScene()
    {
        if (scene is null)
            return;

        var env = scene.Mutate(current => current.Environment);
        environment = new EnvironmentSettings(
            env.Rotation,
            env.VisibleExposure,
            env.LightingExposure,
            env.Visible != 0,
            env.TextureIndex,
            env.DirectionalDirection,
            env.DirectionalIntensity);
        hasEnvironmentState = true;
        RaiseEnvironmentSettingsChanged();
    }

    private void RaiseEnvironmentSettingsChanged() => EnvironmentSettingsChanged?.Invoke(environment);

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        var root = this.GetVisualRoot();
        var scale = root?.RenderScaling ?? -1d;
        var size = Bounds.Size;
        if (size == lastLayoutSize && Math.Abs(scale - lastLayoutScaling) < 0.0001d)
            return;

        lastLayoutSize = size;
        lastLayoutScaling = scale;
        QueueNextFrame();
    }

    private bool IsCurrentLifecycle(long version) => running && lifecycleVersion == version;
}
