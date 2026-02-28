using System;
using System.Runtime.InteropServices;
using System.Threading;
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

public class VulkanControl : Control
{
    private static readonly IBrush HitTestBrush = Brushes.Transparent;
    private static readonly Cursor HiddenCursor = new(StandardCursorType.None);
    private static readonly Cursor DefaultCursor = new(StandardCursorType.Arrow);
    private CompositionSurfaceVisual? visual;
    private Compositor? compositor;
    private bool updateQueued;
    private bool initialized;
    private int recoveryInProgress;
    private readonly Action update;
    private bool isCapturingCamera;
    private bool rightButtonCaptured;
    private Point captureCenterLocal;
    private bool suppressCapturedPointerMove;
    private Task pendingDisposeTask = Task.CompletedTask;

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

    public VulkanControl()
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
            Log.Error(ex, "Failed to initialize VulkanControl.");
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
        }
        catch (VulkanException ex) when (ex.Result == Result.ErrorDeviceLost)
        {
            Log.Error(ex, "Vulkan device lost in VulkanControl. Reinitializing control resources.");
            _ = RecoverFromDeviceLossAsync();
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Render frame failed in VulkanControl; skipping frame.");
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
            "VulkanControl pointer pressed at ({X},{Y}). LeftPressed={LeftPressed} RightPressed={RightPressed}",
            position.X,
            position.Y,
            leftPressed,
            rightPressed);
        input.OnPointerMoved(position);
        Focus(NavigationMethod.Pointer);
        UpdateCaptureFromPointerState(e.Pointer, position, leftPressed, rightPressed);
        e.Handled = isCapturingCamera;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Log.Information("VulkanControl pointer released. Capturing={Capturing}", isCapturingCamera);

        var props = e.GetCurrentPoint(this).Properties;
        UpdateCaptureFromPointerState(e.Pointer, e.GetPosition(this), props.IsLeftButtonPressed, props.IsRightButtonPressed);
        e.Handled = isCapturingCamera;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        UpdateCaptureFromPointerState(e.Pointer, pos, props.IsLeftButtonPressed, props.IsRightButtonPressed);

        if (isCapturingCamera)
        {
            if (suppressCapturedPointerMove)
            {
                suppressCapturedPointerMove = false;
                e.Handled = true;
                return;
            }

            var input = CurrentInput;
            if (input is not null)
            {
                var delta = new System.Numerics.Vector2(
                    (float)(pos.X - captureCenterLocal.X),
                    (float)(pos.Y - captureCenterLocal.Y));

                if (delta.X != 0f || delta.Y != 0f)
                    input.OnPointerDelta(delta);
            }

            if (TryWarpPointerToCaptureCenter())
                suppressCapturedPointerMove = true;

            e.Handled = true;
            return;
        }

        CurrentInput?.OnPointerMoved(pos);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        var props = e.GetCurrentPoint(this).Properties;
        UpdateCaptureFromPointerState(e.Pointer, e.GetPosition(this), props.IsLeftButtonPressed, props.IsRightButtonPressed);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        EndPointerCapture();
        CurrentInput?.OnFocusLost();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Log.Information("VulkanControl wheel changed. DeltaY={DeltaY}", e.Delta.Y);
        CurrentInput?.OnPointerWheel((float)e.Delta.Y);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        Log.Information("VulkanControl key down: {Key}", e.Key);
        CurrentInput?.OnKeyDown(e.Key);
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        Log.Information("VulkanControl key up: {Key}", e.Key);
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
            BeginPointerCapture(pointer, position, rightPressed);
            return;
        }

        EndPointerCapture(pointer);
    }

    private void BeginPointerCapture(IPointer pointer, Point position, bool rightPressed)
    {
        if (!isCapturingCamera)
        {
            pointer.Capture(this);
            Cursor = HiddenCursor;
            isCapturingCamera = true;
            captureCenterLocal = new Point(Bounds.Width * 0.5, Bounds.Height * 0.5);
            if (TryWarpPointerToCaptureCenter())
                suppressCapturedPointerMove = true;
        }

        if (rightPressed && !rightButtonCaptured)
        {
            CurrentInput?.OnPointerPressed(position, rightButton: true);
            rightButtonCaptured = true;
        }
        else if (!rightPressed && rightButtonCaptured)
        {
            CurrentInput?.OnPointerReleased(rightButton: true);
            rightButtonCaptured = false;
        }
    }

    private void EndPointerCapture(IPointer? pointer = null)
    {
        if (rightButtonCaptured)
            CurrentInput?.OnPointerReleased(rightButton: true);

        rightButtonCaptured = false;
        isCapturingCamera = false;
        suppressCapturedPointerMove = false;
        Cursor = DefaultCursor;
        pointer?.Capture(null);
    }

    private bool TryWarpPointerToCaptureCenter()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        var screenPoint = this.PointToScreen(captureCenterLocal);
        return X11PointerWarp.TryWarp(screenPoint.X, screenPoint.Y);
    }

    private static class X11PointerWarp
    {
        private static readonly object Sync = new();
        private static IntPtr display = IntPtr.Zero;
        private static IntPtr rootWindow = IntPtr.Zero;

        public static bool TryWarp(int screenX, int screenY)
        {
            lock (Sync)
            {
                if (!EnsureDisplay())
                    return false;

                XWarpPointer(display, IntPtr.Zero, rootWindow, 0, 0, 0, 0, screenX, screenY);
                XFlush(display);
                return true;
            }
        }

        private static bool EnsureDisplay()
        {
            if (display != IntPtr.Zero && rootWindow != IntPtr.Zero)
                return true;

            display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
                return false;

            var screen = XDefaultScreen(display);
            rootWindow = XRootWindow(display, screen);
            return rootWindow != IntPtr.Zero;
        }

        [DllImport("libX11.so.6")]
        private static extern IntPtr XOpenDisplay(IntPtr displayName);

        [DllImport("libX11.so.6")]
        private static extern int XDefaultScreen(IntPtr x11Display);

        [DllImport("libX11.so.6")]
        private static extern IntPtr XRootWindow(IntPtr x11Display, int screenNumber);

        [DllImport("libX11.so.6")]
        private static extern int XWarpPointer(
            IntPtr x11Display,
            IntPtr srcWindow,
            IntPtr dstWindow,
            int srcX,
            int srcY,
            uint srcWidth,
            uint srcHeight,
            int destX,
            int destY);

        [DllImport("libX11.so.6")]
        private static extern int XFlush(IntPtr x11Display);
    }
}
