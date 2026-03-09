using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Serilog;

namespace UniversalUmap.Rendering.Controls;

internal static class PointerCaptureCoordinator
{
    private static readonly Cursor HiddenCursor = new(StandardCursorType.None);
    private static readonly Cursor DefaultCursor = new(StandardCursorType.Arrow);
    private const double WrapThresholdPixels = 1d;
    private const double WrapInsetPixels = 2d;
    private const int SuppressedMovesAfterWarp = 3;
    private const int RestoreDelayMs = 16;

    private static Control? owner;
    private static bool active;
    private static bool canWarpPointer;
    private static int suppressedWarpMoveCount;
    private static Point lastPointerLocal;
    private static PixelPoint? restoreScreenPoint;

    public static bool IsOwnedBy(Control control)
    {
        if (!active)
            owner = null;

        return ReferenceEquals(owner, control) && active;
    }

    public static bool TryBegin(Control control, IPointer pointer, Point initialPointerPosition)
    {
        if (IsOwnedBy(control))
        {
            Log.Information("Capture.TryBegin already owned by {OwnerType}.", control.GetType().Name);
            return true;
        }
        if (active)
        {
            Log.Information(
                "Capture.TryBegin denied for {RequesterType}; active owner is {OwnerType}.",
                control.GetType().Name,
                owner?.GetType().Name ?? "<null>");
            return false;
        }

        pointer.Capture(control);
        owner = control;
        active = true;
        canWarpPointer = SystemPointerWarp.IsSupported;
        suppressedWarpMoveCount = 0;
        control.Cursor = HiddenCursor;

        var clampedInitial = ClampToBounds(control, initialPointerPosition);
        // Use Avalonia's control-local coordinates as the canonical restore source.
        restoreScreenPoint = control.PointToScreen(clampedInitial);
        lastPointerLocal = clampedInitial;
        Log.Information("Capture.TryBegin success owner={OwnerType} Pos={Pos}", control.GetType().Name, clampedInitial);
        return true;
    }

    public static void End(Control control, IPointer? pointer = null)
    {
        if (!ReferenceEquals(owner, control) || !active)
        {
            Log.Information(
                "Capture.End ignored for {RequesterType}; active={Active} owner={OwnerType}.",
                control.GetType().Name,
                active,
                owner?.GetType().Name ?? "<null>");
            return;
        }

        var restorePoint = restoreScreenPoint;
        var warpWasEnabled = canWarpPointer;

        active = false;
        owner = null;
        suppressedWarpMoveCount = 0;
        control.Cursor = DefaultCursor;
        pointer?.Capture(null);

        if (restorePoint is not null && warpWasEnabled)
        {
            if (RestoreDelayMs > 0)
                Thread.Sleep(RestoreDelayMs);
            if (!SystemPointerWarp.TryWarp(restorePoint.Value.X, restorePoint.Value.Y))
                Log.Warning("Pointer restore warp failed at capture end.");
        }

        canWarpPointer = false;
        restoreScreenPoint = null;
        Log.Information("Capture.End success owner={OwnerType}", control.GetType().Name);
    }

    public static bool TryConsumeWarpSuppressedMove(Control control)
    {
        if (!IsOwnedBy(control) || suppressedWarpMoveCount <= 0)
            return false;

        suppressedWarpMoveCount--;
        return true;
    }

    public static Vector GetDelta(Control control, Point pointerPosition)
    {
        if (!IsOwnedBy(control))
            return default;

        var delta = new Vector(pointerPosition.X - lastPointerLocal.X, pointerPosition.Y - lastPointerLocal.Y);
        lastPointerLocal = pointerPosition;
        return delta;
    }

    public static bool TryWrapAround(Control control, Point pointerPosition)
    {
        if (!IsOwnedBy(control) || !canWarpPointer)
            return false;
        if (!TryGetWrappedPosition(control, pointerPosition, out var wrappedLocal))
            return false;

        if (!TryWarpPointerToPosition(control, wrappedLocal))
        {
            Log.Warning("Pointer warp failed during wrap-around; disabling warp for current capture.");
            canWarpPointer = false;
            return false;
        }

        suppressedWarpMoveCount = SuppressedMovesAfterWarp;
        lastPointerLocal = wrappedLocal;
        return true;
    }

    private static Point ClampToBounds(Control control, Point localPoint)
    {
        var bounds = control.Bounds;
        var maxX = Math.Max(0d, bounds.Width - 1d);
        var maxY = Math.Max(0d, bounds.Height - 1d);
        var x = Math.Clamp(localPoint.X, 0d, maxX);
        var y = Math.Clamp(localPoint.Y, 0d, maxY);
        return new Point(x, y);
    }

    private static bool TryGetWrappedPosition(Control control, Point pointerPosition, out Point wrappedPosition)
    {
        var bounds = control.Bounds;
        if (bounds.Width <= 0d || bounds.Height <= 0d)
        {
            wrappedPosition = pointerPosition;
            return false;
        }

        var maxX = Math.Max(0d, bounds.Width - 1d);
        var maxY = Math.Max(0d, bounds.Height - 1d);
        var wrappedX = pointerPosition.X;
        var wrappedY = pointerPosition.Y;
        var wrapped = false;
        var edgeInsetX = Math.Min(WrapInsetPixels, maxX);
        var edgeInsetY = Math.Min(WrapInsetPixels, maxY);

        if (wrappedX <= WrapThresholdPixels)
        {
            wrappedX = maxX - edgeInsetX;
            wrapped = true;
        }
        else if (wrappedX >= maxX - WrapThresholdPixels)
        {
            wrappedX = edgeInsetX;
            wrapped = true;
        }

        if (wrappedY <= WrapThresholdPixels)
        {
            wrappedY = maxY - edgeInsetY;
            wrapped = true;
        }
        else if (wrappedY >= maxY - WrapThresholdPixels)
        {
            wrappedY = edgeInsetY;
            wrapped = true;
        }

        wrappedPosition = new Point(wrappedX, wrappedY);
        return wrapped;
    }

    private static bool TryWarpPointerToPosition(Control control, Point localPosition)
    {
        var screenPoint = control.PointToScreen(localPosition);
        return SystemPointerWarp.TryWarp(screenPoint.X, screenPoint.Y);
    }

    private static class SystemPointerWarp
    {
        public static bool IsSupported
        {
            get
            {
                if (OperatingSystem.IsWindows())
                    return true;
                if (!OperatingSystem.IsLinux())
                    return false;

                var display = System.Environment.GetEnvironmentVariable("DISPLAY");
                return !string.IsNullOrEmpty(display);
            }
        }

        public static bool TryWarp(int screenX, int screenY)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return Win32PointerWarp.TryWarp(screenX, screenY);
                if (OperatingSystem.IsLinux())
                    return X11PointerWarp.TryWarp(screenX, screenY);
            }
            catch (DllNotFoundException ex)
            {
                Log.Error(ex, "Pointer warp failed: required native library not found.");
                return false;
            }
            catch (EntryPointNotFoundException ex)
            {
                Log.Error(ex, "Pointer warp failed: required native function not found.");
                return false;
            }
            catch (BadImageFormatException ex)
            {
                Log.Error(ex, "Pointer warp failed: native library architecture mismatch.");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Pointer warp failed with unexpected native interop error.");
                return false;
            }

            return false;
        }
    }

    private static class Win32PointerWarp
    {
        public static bool TryWarp(int screenX, int screenY) => SetCursorPos(screenX, screenY);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int x, int y);
    }

    private static class X11PointerWarp
    {
        private static readonly object Sync = new();
        private static IntPtr display = IntPtr.Zero;
        private static IntPtr rootWindow = IntPtr.Zero;
        private static bool loggedOpenDisplayFailure;

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
            {
                if (!loggedOpenDisplayFailure)
                {
                    loggedOpenDisplayFailure = true;
                    Log.Warning(
                        "XOpenDisplay failed; X11 pointer warp unavailable. DISPLAY={Display} WAYLAND_DISPLAY={WaylandDisplay} XDG_SESSION_TYPE={SessionType}",
                        System.Environment.GetEnvironmentVariable("DISPLAY") ?? "<null>",
                        System.Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "<null>",
                        System.Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "<null>");
                }
                return false;
            }

            var screen = XDefaultScreen(display);
            rootWindow = XRootWindow(display, screen);
            if (rootWindow == IntPtr.Zero)
            {
                Log.Warning("XRootWindow returned null; X11 pointer warp unavailable.");
                return false;
            }

            return true;
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

public abstract class CapturingControlBase : ContentControl
{
    private const int HoldCaptureDelayMs = 200;
    private readonly DispatcherTimer holdCaptureTimer;
    private bool holdPressActive;
    private bool holdCaptureStartedDuringPress;
    private long holdPressStartMs;
    private IPointer? holdPointer;
    private Point holdPosition;
    private bool holdLeftPressed;
    private bool holdRightPressed;
    private bool holdPressStartedLeft;
    private bool holdPressStartedRight;
    private bool holdCapturedLeft;
    private bool holdCapturedRight;

    protected CapturingControlBase()
    {
        holdCaptureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldCaptureDelayMs) };
        holdCaptureTimer.Tick += (_, _) => TryBeginHoldCapture();
    }

    protected virtual bool UseTimedHoldCapture => false;
    protected bool IsCaptureActive => PointerCaptureCoordinator.IsOwnedBy(this);

    protected bool BeginCapture(IPointer pointer, Point position)
        => PointerCaptureCoordinator.TryBegin(this, pointer, position);

    protected void EndCapture(IPointer? pointer = null)
        => PointerCaptureCoordinator.End(this, pointer);

    protected bool TryConsumeCaptureWarpMove()
        => PointerCaptureCoordinator.TryConsumeWarpSuppressedMove(this);

    protected Vector GetCaptureDelta(Point position)
        => PointerCaptureCoordinator.GetDelta(this, position);

    protected bool TryWrapCapture(Point position)
        => PointerCaptureCoordinator.TryWrapAround(this, position);

    protected bool CaptureStartedDuringPress => holdCaptureStartedDuringPress;
    protected bool PressStartedWithOnlyLeft => holdPressStartedLeft && !holdPressStartedRight;

    protected virtual void OnHoldPressStarted(PointerPressedEventArgs e, Point position, bool leftPressed, bool rightPressed) { }
    protected virtual void OnHoldPointerMoved(PointerEventArgs e, Point position, bool leftPressed, bool rightPressed) { }
    protected virtual void OnHoldCaptureStarted(Point position, bool leftPressed, bool rightPressed) { }
    protected virtual void OnHoldCaptureDelta(PointerEventArgs e, Point position, Vector delta) { }
    protected virtual void OnHoldCaptureButtonPressed(Point position, bool leftButton, bool rightButton) { }
    protected virtual void OnHoldCaptureButtonReleased(bool leftButton, bool rightButton) { }
    protected virtual void OnHoldCaptureEnded() { }
    protected virtual void OnHoldQuickClick(PointerReleasedEventArgs e, Point releasePosition) { }

    protected void ResetTimedHoldCapture()
    {
        holdCaptureTimer.Stop();
        holdPressActive = false;
        holdCaptureStartedDuringPress = false;
        holdPointer = null;
        holdLeftPressed = false;
        holdRightPressed = false;
        holdPressStartedLeft = false;
        holdPressStartedRight = false;
        holdCapturedLeft = false;
        holdCapturedRight = false;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!UseTimedHoldCapture)
            return;

        var props = e.GetCurrentPoint(this).Properties;
        var leftPressed = props.IsLeftButtonPressed;
        var rightPressed = props.IsRightButtonPressed;
        if (!leftPressed && !rightPressed)
            return;

        holdPressActive = true;
        holdCaptureStartedDuringPress = false;
        holdPressStartMs = System.Environment.TickCount64;
        holdPointer = e.Pointer;
        holdPosition = e.GetPosition(this);
        holdLeftPressed = leftPressed;
        holdRightPressed = rightPressed;
        holdPressStartedLeft = leftPressed;
        holdPressStartedRight = rightPressed;

        OnHoldPressStarted(e, holdPosition, leftPressed, rightPressed);

        holdCaptureTimer.Stop();
        holdCaptureTimer.Start();
        e.Handled = IsCaptureActive;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!UseTimedHoldCapture)
            return;

        var position = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        var leftPressed = props.IsLeftButtonPressed;
        var rightPressed = props.IsRightButtonPressed;

        if (holdPressActive)
        {
            if (!leftPressed && !rightPressed)
            {
                holdCaptureTimer.Stop();
                holdPointer = null;
                holdLeftPressed = false;
                holdRightPressed = false;
            }
            else
            {
                holdPointer ??= e.Pointer;
                holdPosition = position;
                holdLeftPressed = leftPressed;
                holdRightPressed = rightPressed;
            }
        }

        if (IsCaptureActive && TryConsumeCaptureWarpMove())
        {
            e.Handled = true;
            return;
        }

        if (!IsCaptureActive)
        {
            OnHoldPointerMoved(e, position, leftPressed, rightPressed);
            return;
        }

        var delta = GetCaptureDelta(position);
        OnHoldCaptureDelta(e, position, delta);
        SyncHeldCaptureButtons(position, leftPressed, rightPressed);

        if (!holdCapturedLeft && !holdCapturedRight)
        {
            EndCapture(e.Pointer);
            OnHoldCaptureEnded();
        }
        else
        {
            TryWrapCapture(position);
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!UseTimedHoldCapture)
            return;

        var releasePosition = e.GetPosition(this);
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

        if (IsCaptureActive)
        {
            SyncHeldCaptureButtons(releasePosition, leftPressed, rightPressed);
            if (!holdCapturedLeft && !holdCapturedRight)
            {
                EndCapture(e.Pointer);
                OnHoldCaptureEnded();
            }
        }

        var elapsedMs = System.Environment.TickCount64 - holdPressStartMs;
        if (holdPressActive && !holdCaptureStartedDuringPress && elapsedMs < HoldCaptureDelayMs)
            OnHoldQuickClick(e, releasePosition);

        ResetTimedHoldCapture();
        e.Handled = IsCaptureActive;
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        if (!UseTimedHoldCapture)
            return;

        ResetTimedHoldCapture();
        if (IsCaptureActive)
        {
            ReleaseHeldCaptureButtons();
            EndCapture();
            OnHoldCaptureEnded();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!UseTimedHoldCapture)
            return;

        ResetTimedHoldCapture();
        if (IsCaptureActive)
        {
            ReleaseHeldCaptureButtons();
            EndCapture();
            OnHoldCaptureEnded();
        }
    }

    private void TryBeginHoldCapture()
    {
        holdCaptureTimer.Stop();
        if (!holdPressActive || IsCaptureActive || holdPointer is null || (!holdLeftPressed && !holdRightPressed))
            return;

        if (!BeginCapture(holdPointer, holdPosition))
            return;

        holdCaptureStartedDuringPress = true;
        OnHoldCaptureStarted(holdPosition, holdLeftPressed, holdRightPressed);
        SyncHeldCaptureButtons(holdPosition, holdLeftPressed, holdRightPressed);
    }

    private void SyncHeldCaptureButtons(Point position, bool leftPressed, bool rightPressed)
    {
        if (leftPressed && !holdCapturedLeft)
        {
            OnHoldCaptureButtonPressed(position, leftButton: true, rightButton: false);
            holdCapturedLeft = true;
        }
        else if (!leftPressed && holdCapturedLeft)
        {
            OnHoldCaptureButtonReleased(leftButton: true, rightButton: false);
            holdCapturedLeft = false;
        }

        if (rightPressed && !holdCapturedRight)
        {
            OnHoldCaptureButtonPressed(position, leftButton: false, rightButton: true);
            holdCapturedRight = true;
        }
        else if (!rightPressed && holdCapturedRight)
        {
            OnHoldCaptureButtonReleased(leftButton: false, rightButton: true);
            holdCapturedRight = false;
        }
    }

    private void ReleaseHeldCaptureButtons()
    {
        if (holdCapturedLeft)
            OnHoldCaptureButtonReleased(leftButton: true, rightButton: false);
        if (holdCapturedRight)
            OnHoldCaptureButtonReleased(leftButton: false, rightButton: true);

        holdCapturedLeft = false;
        holdCapturedRight = false;
    }
}
