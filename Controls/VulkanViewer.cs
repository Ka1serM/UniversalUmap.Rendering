using System;
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

public class VulkanViewer : CapturingControlBase
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
    private bool updateQueued;
    private bool initialized;
    private readonly Action update;
    private Task pendingDisposeTask = Task.CompletedTask;

    private sealed class GraphicsResources : IAsyncDisposable
    {
        public SharedRendererContext.Lease RendererLease { get; }
        public Renderer Renderer { get; }
        public VulkanSurface Surface { get; }

        public GraphicsResources(SharedRendererContext.Lease rendererLease, VulkanSurface surface)
        {
            RendererLease = rendererLease;
            Renderer = rendererLease.Renderer;
            Surface = surface;
        }

        public async ValueTask DisposeAsync()
        {
            await Surface.DisposeAsync();
            RendererLease.Dispose();
        }
    }

    private GraphicsResources? resources;
    private Input? CurrentInput => resources?.Renderer.Input;

    protected override bool UseTimedHoldCapture => true;

    public event Action? FrameRendered;
    public event Action? DebugOverlayToggleRequested;

    public string SelectedInstanceName => resources?.Renderer.Scene.SelectedInstance?.Name ?? "<none>";
    public Vector3 CameraPositionDebug => resources?.Renderer.Scene.CameraController.Position ?? Vector3.Zero;
    public Vector3 ArcballPivotDebug => resources?.Renderer.Scene.CameraController.ArcballPivot ?? Vector3.Zero;

    public VulkanViewer()
    {
        update = UpdateFrame;
        Focusable = true;
        IsTabStop = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Initialize();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ResetTimedHoldCapture();
        EndCapture();

        if (initialized)
            FreeGraphicsResources();

        initialized = false;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(HitTestBrush, new Rect(Bounds.Size));
    }

    protected override void OnHoldPressStarted(PointerPressedEventArgs e, Point position, bool leftPressed, bool rightPressed)
    {
        var input = CurrentInput;
        if (input is null)
            return;

        input.SetModifierState(e.KeyModifiers);
        input.OnPointerMoved(position);
        Focus(NavigationMethod.Pointer);
    }

    protected override void OnHoldPointerMoved(PointerEventArgs e, Point position, bool leftPressed, bool rightPressed)
    {
        CurrentInput?.SetModifierState(e.KeyModifiers);
        CurrentInput?.OnPointerMoved(position);
    }

    protected override void OnHoldCaptureDelta(PointerEventArgs e, Point position, Avalonia.Vector delta)
    {
        CurrentInput?.SetModifierState(e.KeyModifiers);
        if (delta.X == 0d && delta.Y == 0d)
            return;

        CurrentInput?.OnPointerDelta(new System.Numerics.Vector2((float)delta.X, (float)delta.Y));
    }

    protected override void OnHoldCaptureButtonPressed(Point position, bool leftButton, bool rightButton)
    {
        CurrentInput?.OnPointerPressed(position, leftButton, rightButton);
    }

    protected override void OnHoldCaptureButtonReleased(bool leftButton, bool rightButton)
    {
        CurrentInput?.OnPointerReleased(leftButton, rightButton);
    }

    protected override void OnHoldQuickClick(PointerReleasedEventArgs e, Point releasePosition)
    {
        CurrentInput?.SetModifierState(e.KeyModifiers);

        if (PressStartedWithOnlyLeft &&
            e.InitialPressMouseButton == MouseButton.Left &&
            Bounds.Contains(releasePosition) &&
            !CaptureStartedDuringPress)
            HandleSelectionClick(releasePosition);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        CurrentInput?.SetModifierState(e.KeyModifiers);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        CurrentInput?.OnFocusLost();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        CurrentInput?.SetModifierState(e.KeyModifiers);
        CurrentInput?.OnPointerWheel((float)e.Delta.Y);
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

        CurrentInput?.OnKeyDown(e.Key);
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        CurrentInput?.OnKeyUp(e.Key);
        e.Handled = true;
    }

    private async void Initialize()
    {
        try
        {
            await pendingDisposeTask;

            var selfVisual = ElementComposition.GetElementVisual(this)!;
            compositor = selfVisual.Compositor;
            var drawingSurface = compositor.CreateDrawingSurface();
            visual = compositor.CreateSurfaceVisual();
            visual.Size = new(Bounds.Width, Bounds.Height);
            visual.Surface = drawingSurface;
            ElementComposition.SetElementChildVisual(this, visual);

            var interop = await compositor.TryGetCompositionGpuInterop();
            if (interop is null)
            {
                initialized = false;
                return;
            }

            var rendererLease = await SharedRendererContext.AcquireAsync(compositor);
            if (rendererLease is null)
            {
                initialized = false;
                return;
            }

            var surface = new VulkanSurface(rendererLease.Renderer.Context, interop, drawingSurface);
            resources = new GraphicsResources(rendererLease, surface);
            initialized = true;
            QueueNextFrame();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize VulkanViewer.");
            initialized = false;
        }
    }

    private void ReinitializeAfterDeviceLoss()
    {
        initialized = false;
        updateQueued = false;
        ResetTimedHoldCapture();
        EndCapture();
        CurrentInput?.OnFocusLost();
        SharedRendererContext.Reset();
        FreeGraphicsResources();
        Initialize();
    }

    private void FreeGraphicsResources()
    {
        if (resources is null)
            return;

        var toDispose = resources;
        resources = null;
        var previousDisposeTask = pendingDisposeTask;
        pendingDisposeTask = DisposeSurfaceChainAsync(previousDisposeTask, toDispose);
    }

    private static async Task DisposeSurfaceChainAsync(Task previousDisposeTask, GraphicsResources surfaceResources)
    {
        try
        {
            await previousDisposeTask;
            await surfaceResources.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed disposing Vulkan surface resources.");
        }
    }

    private void UpdateFrame()
    {
        updateQueued = false;
        var root = this.GetVisualRoot();
        if (root is null || visual is null || resources is null)
            return;

        var pixelSize = PixelSize.FromSize(Bounds.Size, root.RenderScaling);
        visual.Size = new(Bounds.Width, Bounds.Height);
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
        {
            QueueNextFrame();
            return;
        }

        try
        {
            if (!resources.Surface.TryBeginDraw(pixelSize, out var image, out var presentScope) || presentScope is null)
            {
                QueueNextFrame();
                return;
            }

            using (presentScope)
                resources.Renderer.Render(image);

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

    private void QueueNextFrame()
    {
        if (!initialized || updateQueued || compositor is null)
            return;

        updateQueued = true;
        compositor.RequestCompositionUpdate(update);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == BoundsProperty)
            QueueNextFrame();

        base.OnPropertyChanged(change);
    }

    private void HandleSelectionClick(Point localPosition)
    {
        if (resources is null)
            return;

        resources.Renderer.Picker.TryPickInstance(
            localPosition,
            Bounds.Size,
            out _,
            out _,
            out _,
            out _);
    }

    public bool TryGetEnvironmentSettings(out EnvironmentSettings settings)
    {
        var renderer = resources?.Renderer;
        if (renderer is null)
        {
            settings = default;
            return false;
        }

        var env = renderer.Scene.Mutate(scene => scene.Environment);
        settings = new EnvironmentSettings(
            env.Rotation,
            env.VisibleExposure,
            env.LightingExposure,
            env.Visible != 0,
            env.TextureIndex,
            env.DirectionalDirection,
            env.DirectionalIntensity);
        return true;
    }

    public void SetDirectionalLightIntensity(float intensity)
    {
        var renderer = resources?.Renderer;
        if (renderer is null)
            return;

        renderer.Scene.Mutate(scene =>
        {
            var env = scene.Environment;
            env.DirectionalIntensity = intensity;
            scene.SetEnvironmentData(env);
        });
    }

    public void SetDirectionalLightDirection(Vector3 direction)
    {
        var renderer = resources?.Renderer;
        if (renderer is null)
            return;

        var normalizedDirection = direction.LengthSquared() > 0.000001f
            ? Vector3.Normalize(direction)
            : new Vector3(0f, 1f, 0f);

        renderer.Scene.Mutate(scene =>
        {
            var env = scene.Environment;
            env.DirectionalDirection = normalizedDirection;
            scene.SetEnvironmentData(env);
        });
    }

    public void SetEnvironmentRotation(float rotationDegrees)
    {
        var renderer = resources?.Renderer;
        if (renderer is null)
            return;

        renderer.Scene.Mutate(scene =>
        {
            var env = scene.Environment;
            env.Rotation = rotationDegrees;
            scene.SetEnvironmentData(env);
        });
    }

    public void SetEnvironmentVisibleExposure(float exposureStops)
    {
        var renderer = resources?.Renderer;
        if (renderer is null)
            return;

        renderer.Scene.Mutate(scene =>
        {
            var env = scene.Environment;
            env.VisibleExposure = exposureStops;
            scene.SetEnvironmentData(env);
        });
    }

    public void SetEnvironmentLightingExposure(float exposureStops)
    {
        var renderer = resources?.Renderer;
        if (renderer is null)
            return;

        renderer.Scene.Mutate(scene =>
        {
            var env = scene.Environment;
            env.LightingExposure = exposureStops;
            scene.SetEnvironmentData(env);
        });
    }

    public void SetEnvironmentVisible(bool visible)
    {
        var renderer = resources?.Renderer;
        if (renderer is null)
            return;

        renderer.Scene.Mutate(scene =>
        {
            var env = scene.Environment;
            env.Visible = visible ? 1 : 0;
            scene.SetEnvironmentData(env);
        });
    }
}
