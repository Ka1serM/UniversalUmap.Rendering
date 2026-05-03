using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace UniversalUmap.Rendering.Controls;

internal static class PointerCapture
{
    private static readonly Cursor HiddenCursor = new(StandardCursorType.None);

    private static Control? owner;
    private static IPointer? pointer;
    private static TopLevel? topLevel;
    private static Cursor? previousOwnerCursor;
    private static Cursor? previousTopLevelCursor;
    private static Point lastPosition;

    public static bool IsOwnedBy(Control control)
        => ReferenceEquals(owner, control);

    public static bool TryBegin(Control control, IPointer targetPointer, Point initialPosition)
    {
        if (IsOwnedBy(control))
            return true;

        if (owner is not null)
        {
            if (ReferenceEquals(pointer?.Captured, owner))
                return false;

            End(owner);
        }

        targetPointer.Capture(control);
        if (!ReferenceEquals(targetPointer.Captured, control))
            return false;

        owner = control;
        pointer = targetPointer;
        topLevel = TopLevel.GetTopLevel(control);
        previousOwnerCursor = control.Cursor;
        previousTopLevelCursor = topLevel?.Cursor;
        lastPosition = CoerceFinite(initialPosition, default);

        HideCursor(control);
        return true;
    }

    public static Vector UpdateMove(Control control, Point position)
    {
        if (!IsOwnedBy(control))
            return default;

        var current = CoerceFinite(position, lastPosition);
        var delta = current - lastPosition;
        lastPosition = current;
        return delta;
    }

    public static void End(Control control, IPointer? targetPointer = null)
    {
        if (!IsOwnedBy(control))
            return;

        RestoreCursor(control, previousOwnerCursor);
        if (topLevel is not null)
            RestoreCursor(topLevel, previousTopLevelCursor);

        var capturedPointer = targetPointer ?? pointer;
        if (capturedPointer is not null && ReferenceEquals(capturedPointer.Captured, control))
            capturedPointer.Capture(null);

        owner = null;
        pointer = null;
        topLevel = null;
        previousOwnerCursor = null;
        previousTopLevelCursor = null;
        lastPosition = default;
    }

    private static void HideCursor(Control control)
    {
        control.Cursor = HiddenCursor;
        if (topLevel is not null)
            topLevel.Cursor = HiddenCursor;
    }

    private static void RestoreCursor(InputElement element, Cursor? cursor)
    {
        if (cursor is null)
            element.ClearValue(InputElement.CursorProperty);
        else
            element.Cursor = cursor;
    }

    private static Point CoerceFinite(Point point, Point fallback)
    {
        var x = double.IsFinite(point.X) ? point.X : fallback.X;
        var y = double.IsFinite(point.Y) ? point.Y : fallback.Y;
        return new Point(x, y);
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
    protected bool IsCaptureActive => PointerCapture.IsOwnedBy(this);

    protected bool BeginCapture(IPointer pointer, Point position)
        => PointerCapture.TryBegin(this, pointer, position);

    protected void EndCapture(IPointer? pointer = null)
        => PointerCapture.End(this, pointer);

    protected Vector GetCaptureDelta(Point position)
        => PointerCapture.UpdateMove(this, position);

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
        holdPressStartMs = Environment.TickCount64;
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

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!UseTimedHoldCapture)
            return;

        var releasePosition = e.GetPosition(this);
        var handled = IsCaptureActive;
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

        var elapsedMs = Environment.TickCount64 - holdPressStartMs;
        if (holdPressActive && !holdCaptureStartedDuringPress && elapsedMs < HoldCaptureDelayMs)
        {
            OnHoldQuickClick(e, releasePosition);
            handled = true;
        }

        ResetTimedHoldCapture();
        e.Handled = e.Handled || handled;
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        if (!UseTimedHoldCapture)
            return;

        EndActiveHoldCapture();
        ResetTimedHoldCapture();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!UseTimedHoldCapture)
            return;

        var handled = IsCaptureActive;
        EndActiveHoldCapture();
        ResetTimedHoldCapture();
        e.Handled = e.Handled || handled;
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

    private void EndActiveHoldCapture()
    {
        if (!IsCaptureActive)
            return;

        ReleaseHeldCaptureButtons();
        EndCapture();
        OnHoldCaptureEnded();
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
