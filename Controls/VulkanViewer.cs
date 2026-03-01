using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
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

namespace UniversalUmap.Rendering.Controls;

public class VulkanViewer : Control
{
    private static readonly IBrush HitTestBrush = Brushes.Transparent;
    private const double CaptureHoldDelayMs = 180d;
    private const double SelectionClickMoveThreshold = 4d;
    private CompositionSurfaceVisual? visual;
    private Compositor? compositor;
    private bool updateQueued;
    private bool initialized;
    private int recoveryInProgress;
    private readonly Action update;
    private readonly PointerCaptureController pointerCapture = new();
    private bool leftButtonCaptured;
    private bool rightButtonCaptured;
    private Task pendingDisposeTask = Task.CompletedTask;
    private readonly Stopwatch frameTimer = Stopwatch.StartNew();
    private double overlaySampleStartSeconds;
    private int overlayFrameCount;
    private float overlayFps;
    private bool showDebugOverlay = true;
    private string? lastPickSummary;
    private Point pressPointerPosition;
    private bool captureMoved;
    private bool pickOnLeftRelease;
    private bool pendingCapture;
    private IPointer? pendingCapturePointer;
    private bool pendingCaptureLeftPressed;
    private bool pendingCaptureRightPressed;
    private readonly DispatcherTimer captureDelayTimer = new() { Interval = TimeSpan.FromMilliseconds(CaptureHoldDelayMs) };

    private sealed class GraphicsResources : IAsyncDisposable
    {
        public Renderer Renderer { get; }
        public VulkanSurface Surface { get; }

        public GraphicsResources(Renderer renderer, VulkanSurface surface)
        {
            Renderer = renderer;
            Surface = surface;
        }

        public async ValueTask DisposeAsync()
        {
            await Surface.DisposeAsync();
            Renderer.Dispose();
        }
    }

    private GraphicsResources? resources;
    private Input? CurrentInput => resources?.Renderer.Input;
    public bool ShowDebugOverlay => showDebugOverlay;
    public float OverlayFps => overlayFps;
    public string LastPickSummary => lastPickSummary ?? "<none>";
    public string SelectedInstanceName => resources?.Renderer.Scene.SelectedInstance?.Name ?? "<none>";
    public Vector3 CameraPositionDebug => resources?.Renderer.Scene.CameraController.Position ?? Vector3.Zero;
    public Vector3 ArcballPivotDebug => resources?.Renderer.Scene.CameraController.ArcballPivot ?? Vector3.Zero;

    public VulkanViewer()
    {
        update = UpdateFrame;
        Focusable = true;
        IsTabStop = true;
        captureDelayTimer.Tick += (_, _) =>
        {
            captureDelayTimer.Stop();
            if (!pendingCapture || pointerCapture.IsActive || pendingCapturePointer is null)
                return;

            BeginPointerCapture(
                pendingCapturePointer,
                pressPointerPosition,
                pendingCaptureLeftPressed,
                pendingCaptureRightPressed);
            EndPendingCapture();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Initialize();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(HitTestBrush, new Rect(Bounds.Size));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EndPointerCapture();
        if (initialized)
            FreeGraphicsResources();

        initialized = false;
        base.OnDetachedFromVisualTree(e);
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

            var renderer = new Renderer(interop);
            var surface = new VulkanSurface(renderer.Context, interop, drawingSurface);
            resources = new GraphicsResources(renderer, surface);
            RendererHost.Register(renderer);
            initialized = true;
            QueueNextFrame();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize VulkanViewer.");
            initialized = false;
        }
    }

    private void FreeGraphicsResources()
    {
        if (resources is null)
            return;

        var toDispose = resources;
        RendererHost.Unregister(toDispose.Renderer);
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
        if (Volatile.Read(ref recoveryInProgress) != 0)
        {
            QueueNextFrame();
            return;
        }

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

            UpdateOverlayFps();
            InvalidateVisual();
        }
        catch (VulkanException ex) when (ex.Result == Result.ErrorDeviceLost)
        {
            Log.Error(ex, "Vulkan device lost in VulkanViewer. Reinitializing control resources.");
            _ = RecoverFromDeviceLossAsync();
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

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var input = CurrentInput;
        if (input is null)
            return;

        var position = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        var leftPressed = props.IsLeftButtonPressed;
        var rightPressed = props.IsRightButtonPressed;
        Log.Information(
            "VulkanViewer pointer pressed at ({X},{Y}). LeftPressed={LeftPressed} RightPressed={RightPressed}",
            position.X,
            position.Y,
            leftPressed,
            rightPressed);

        input.SetModifierState(e.KeyModifiers);
        input.OnPointerMoved(position);
        Focus(NavigationMethod.Pointer);
        pressPointerPosition = position;
        captureMoved = false;
        pickOnLeftRelease = leftPressed && !rightPressed;

        if (pointerCapture.IsActive)
            UpdateCaptureFromPointerState(e.Pointer, position, leftPressed, rightPressed);
        else
            BeginPendingCapture(e.Pointer, leftPressed, rightPressed);
        e.Handled = pointerCapture.IsActive;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Log.Information("VulkanViewer pointer released. Capturing={Capturing}", pointerCapture.IsActive);

        CurrentInput?.SetModifierState(e.KeyModifiers);
        var props = e.GetCurrentPoint(this).Properties;
        var leftPressed = props.IsLeftButtonPressed || leftButtonCaptured;
        var rightPressed = props.IsRightButtonPressed || rightButtonCaptured;
        switch (e.InitialPressMouseButton)
        {
            case MouseButton.Left:
                leftPressed = false;
                break;
            case MouseButton.Right:
                rightPressed = false;
                break;
        }
        var releasePosition = e.GetPosition(this);
        if (pointerCapture.IsActive)
            UpdateCaptureFromPointerState(e.Pointer, releasePosition, leftPressed, rightPressed);
        else
            EndPendingCapture();

        var dx = releasePosition.X - pressPointerPosition.X;
        var dy = releasePosition.Y - pressPointerPosition.Y;
        var releaseMoved = (dx * dx) + (dy * dy) >
                           (SelectionClickMoveThreshold * SelectionClickMoveThreshold);
        var shouldPick = e.InitialPressMouseButton == MouseButton.Left &&
                         pickOnLeftRelease &&
                         !captureMoved &&
                         !releaseMoved &&
                         Bounds.Contains(releasePosition);
        if (shouldPick)
            HandleSelectionClick(releasePosition);

        pickOnLeftRelease = false;
        e.Handled = pointerCapture.IsActive;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        var wasCapturing = pointerCapture.IsActive;
        CurrentInput?.SetModifierState(e.KeyModifiers);
        var props = e.GetCurrentPoint(this).Properties;

        if (pointerCapture.IsActive && pointerCapture.TryConsumeWarpSuppressedMove())
        {
            e.Handled = true;
            return;
        }

        // Keep capture stable across warp/capture edge cases where one move event may report no buttons.
        var leftPressed = props.IsLeftButtonPressed || leftButtonCaptured;
        var rightPressed = props.IsRightButtonPressed || rightButtonCaptured;
        if (pointerCapture.IsActive)
            UpdateCaptureFromPointerState(e.Pointer, pos, leftPressed, rightPressed);
        else if (!leftPressed && !rightPressed)
            EndPendingCapture();

        // Ignore movement on the transition frame into capture to avoid applying a large one-frame delta.
        if (!wasCapturing && pointerCapture.IsActive)
        {
            e.Handled = true;
            return;
        }

        if (pointerCapture.IsActive)
        {
            var input = CurrentInput;
            if (input is not null)
            {
                var pointerDelta = pointerCapture.GetDeltaFromCenter(pos);
                var delta = new System.Numerics.Vector2((float)pointerDelta.X, (float)pointerDelta.Y);

                if (delta.X != 0f || delta.Y != 0f)
                {
                    input.OnPointerDelta(delta);
                    captureMoved = true;
                }
            }

            pointerCapture.Recenter(this);

            e.Handled = true;
            return;
        }

        CurrentInput?.OnPointerMoved(pos);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        CurrentInput?.SetModifierState(e.KeyModifiers);
        var props = e.GetCurrentPoint(this).Properties;
        var leftPressed = props.IsLeftButtonPressed || leftButtonCaptured;
        var rightPressed = props.IsRightButtonPressed || rightButtonCaptured;
        if (pointerCapture.IsActive)
            UpdateCaptureFromPointerState(e.Pointer, e.GetPosition(this), leftPressed, rightPressed);
        else if (leftPressed || rightPressed)
            BeginPendingCapture(e.Pointer, leftPressed, rightPressed);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        EndPointerCapture();
        EndPendingCapture();
        CurrentInput?.OnFocusLost();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Log.Information("VulkanViewer wheel changed. DeltaY={DeltaY}", e.Delta.Y);
        CurrentInput?.SetModifierState(e.KeyModifiers);
        CurrentInput?.OnPointerWheel((float)e.Delta.Y);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F3)
        {
            showDebugOverlay = !showDebugOverlay;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        Log.Information("VulkanViewer key down: {Key}", e.Key);
        CurrentInput?.OnKeyDown(e.Key);
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        Log.Information("VulkanViewer key up: {Key}", e.Key);
        CurrentInput?.OnKeyUp(e.Key);
        e.Handled = true;
    }

    private async Task RecoverFromDeviceLossAsync()
    {
        if (Interlocked.Exchange(ref recoveryInProgress, 1) != 0)
            return;

        initialized = false;
        updateQueued = false;
        EndPointerCapture();
        CurrentInput?.OnFocusLost();

        FreeGraphicsResources();
        await pendingDisposeTask;

        Interlocked.Exchange(ref recoveryInProgress, 0);
        Initialize();
    }

    private void UpdateCaptureFromPointerState(IPointer pointer, Point position, bool leftPressed, bool rightPressed)
    {
        if (leftPressed || rightPressed)
        {
            BeginPointerCapture(pointer, position, leftPressed, rightPressed);
            return;
        }

        EndPointerCapture(pointer);
    }

    private void BeginPointerCapture(IPointer pointer, Point position, bool leftPressed, bool rightPressed)
    {
        pointerCapture.Begin(this, pointer);

        if (leftPressed && !leftButtonCaptured)
        {
            CurrentInput?.OnPointerPressed(position, leftButton: true, rightButton: false);
            leftButtonCaptured = true;
        }
        else if (!leftPressed && leftButtonCaptured)
        {
            CurrentInput?.OnPointerReleased(leftButton: true, rightButton: false);
            leftButtonCaptured = false;
        }

        if (rightPressed && !rightButtonCaptured)
        {
            CurrentInput?.OnPointerPressed(position, leftButton: false, rightButton: true);
            rightButtonCaptured = true;
        }
        else if (!rightPressed && rightButtonCaptured)
        {
            CurrentInput?.OnPointerReleased(leftButton: false, rightButton: true);
            rightButtonCaptured = false;
        }
    }

    private void EndPointerCapture(IPointer? pointer = null)
    {
        if (leftButtonCaptured)
            CurrentInput?.OnPointerReleased(leftButton: true, rightButton: false);
        if (rightButtonCaptured)
            CurrentInput?.OnPointerReleased(leftButton: false, rightButton: true);

        leftButtonCaptured = false;
        rightButtonCaptured = false;
        pointerCapture.End(this, pointer);
    }

    private void BeginPendingCapture(IPointer pointer, bool leftPressed, bool rightPressed)
    {
        if (!leftPressed && !rightPressed)
        {
            EndPendingCapture();
            return;
        }

        pendingCapture = true;
        pendingCapturePointer = pointer;
        pendingCaptureLeftPressed = leftPressed;
        pendingCaptureRightPressed = rightPressed;
        captureDelayTimer.Stop();
        captureDelayTimer.Start();
    }

    private void EndPendingCapture()
    {
        captureDelayTimer.Stop();
        pendingCapture = false;
        pendingCapturePointer = null;
        pendingCaptureLeftPressed = false;
        pendingCaptureRightPressed = false;
    }

    private void HandleSelectionClick(Point localPosition)
    {
        if (resources is null)
            return;

        if (resources.Renderer.Picker.TryPickInstance(
                localPosition,
                Bounds.Size,
                out var instance,
                out var instanceId,
                out var pixelX,
                out var pixelY))
        {
            lastPickSummary = $"{instance?.Name ?? "<unnamed>"} (id={instanceId})";
            Log.Information(
                "Picked instance at ({X},{Y}) => id={InstanceId}, name={InstanceName}",
                pixelX,
                pixelY,
                instanceId,
                instance?.Name ?? "<unnamed>");
        }
        else
        {
            lastPickSummary = "none";
            Log.Information("Picked instance at ({X},{Y}) => none", pixelX, pixelY);
        }

        InvalidateVisual();
    }

    private void UpdateOverlayFps()
    {
        var nowSeconds = frameTimer.Elapsed.TotalSeconds;
        if (overlaySampleStartSeconds <= 0)
            overlaySampleStartSeconds = nowSeconds;

        overlayFrameCount++;
        var elapsedSeconds = nowSeconds - overlaySampleStartSeconds;
        if (elapsedSeconds < 0.25d)
            return;

        overlayFps = (float)(overlayFrameCount / elapsedSeconds);
        overlayFrameCount = 0;
        overlaySampleStartSeconds = nowSeconds;
    }

}
