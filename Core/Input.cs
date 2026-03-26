using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Avalonia;
using Avalonia.Input;
using Serilog;

namespace UniversalUmap.Rendering.Core;

internal sealed class Input
{
    internal readonly record struct FrameSnapshot(
        Vector2 MouseDelta,
        float WheelDelta,
        bool LeftMouseDown,
        bool RightMouseDown,
        bool MoveForward,
        bool MoveBackward,
        bool MoveLeft,
        bool MoveRight,
        bool MoveDown,
        bool MoveUp,
        bool Boost);

    private const int PointerLeftMask = 1 << 0;
    private const int PointerRightMask = 1 << 1;
    private const int MoveForwardMask = 1 << 0;
    private const int MoveBackwardMask = 1 << 1;
    private const int MoveLeftMask = 1 << 2;
    private const int MoveRightMask = 1 << 3;
    private const int MoveDownMask = 1 << 4;
    private const int MoveUpMask = 1 << 5;
    private const int BoostMask = 1 << 6;

    private readonly object sync = new();
    private int pointerButtons;
    private int keyState;
    private Point lastPointerPosition;
    private bool hasPointerPosition;
    private Vector2 pendingMouseDelta;
    private float pendingWheelDelta;

    public void OnPointerPressed(Point position, bool leftButton, bool rightButton)
    {
        if (leftButton)
            SetPointerButton(PointerLeftMask, down: true);
        if (rightButton)
            SetPointerButton(PointerRightMask, down: true);

        lock (sync)
        {
            lastPointerPosition = position;
            hasPointerPosition = true;
            pendingMouseDelta = Vector2.Zero;
        }
        Log.Debug(
            "Input pointer pressed. LeftButton={LeftButton} RightButton={RightButton} Pos=({X},{Y})",
            leftButton,
            rightButton,
            position.X,
            position.Y);
    }

    public void OnPointerReleased(bool leftButton, bool rightButton)
    {
        if (!leftButton && !rightButton)
            return;

        if (leftButton)
            SetPointerButton(PointerLeftMask, down: false);
        if (rightButton)
            SetPointerButton(PointerRightMask, down: false);

        lock (sync)
        {
            if ((Volatile.Read(ref pointerButtons) & (PointerLeftMask | PointerRightMask)) == 0)
                hasPointerPosition = false;
            pendingMouseDelta = Vector2.Zero;
        }
        Log.Debug(
            "Input pointer released. LeftButton={LeftButton} RightButton={RightButton}",
            leftButton,
            rightButton);
    }

    public void OnPointerMoved(Point position)
    {
        lock (sync)
        {
            if (!hasPointerPosition)
            {
                lastPointerPosition = position;
                hasPointerPosition = true;
                return;
            }

            pendingMouseDelta += new Vector2((float)(position.X - lastPointerPosition.X), (float)(position.Y - lastPointerPosition.Y));
            lastPointerPosition = position;
        }
    }

    public void OnPointerDelta(Vector2 delta)
    {
        lock (sync)
            pendingMouseDelta += delta;
    }

    public void OnPointerWheel(float deltaY)
    {
        lock (sync)
            pendingWheelDelta += deltaY;
        Log.Debug("Input wheel delta={Delta}", deltaY);
    }

    public void OnKeyDown(Key key)
    {
        UpdateKeyState(key, down: true);
        Log.Debug("Input key down: {Key}", key);
    }

    public void OnKeyUp(Key key)
    {
        UpdateKeyState(key, down: false);
        Log.Debug("Input key up: {Key}", key);
    }

    public void OnFocusLost()
    {
        Volatile.Write(ref pointerButtons, 0);
        Volatile.Write(ref keyState, 0);

        lock (sync)
        {
            hasPointerPosition = false;
            pendingMouseDelta = Vector2.Zero;
            pendingWheelDelta = 0f;
        }
        Log.Debug("Input focus lost; transient state cleared.");
    }

    public void SetModifierState(KeyModifiers modifiers)
    {
        SetKeyFlag(BoostMask, (modifiers & KeyModifiers.Shift) != 0);
    }

    public FrameSnapshot ConsumeFrameSnapshot()
    {
        Vector2 mouseDelta;
        float wheelDelta;
        lock (sync)
        {
            mouseDelta = pendingMouseDelta;
            wheelDelta = pendingWheelDelta;
            pendingMouseDelta = Vector2.Zero;
            pendingWheelDelta = 0f;
        }

        var pointerState = Volatile.Read(ref pointerButtons);
        var keys = Volatile.Read(ref keyState);
        return new FrameSnapshot(
            mouseDelta,
            wheelDelta,
            (pointerState & PointerLeftMask) != 0,
            (pointerState & PointerRightMask) != 0,
            (keys & MoveForwardMask) != 0,
            (keys & MoveBackwardMask) != 0,
            (keys & MoveLeftMask) != 0,
            (keys & MoveRightMask) != 0,
            (keys & MoveDownMask) != 0,
            (keys & MoveUpMask) != 0,
            (keys & BoostMask) != 0);
    }

    private void UpdateKeyState(Key key, bool down)
    {
        switch (key)
        {
            case Key.W:
                SetKeyFlag(MoveForwardMask, down);
                break;
            case Key.S:
                SetKeyFlag(MoveBackwardMask, down);
                break;
            case Key.A:
                SetKeyFlag(MoveLeftMask, down);
                break;
            case Key.D:
                SetKeyFlag(MoveRightMask, down);
                break;
            case Key.Q:
                SetKeyFlag(MoveDownMask, down);
                break;
            case Key.E:
                SetKeyFlag(MoveUpMask, down);
                break;
            case Key.LeftShift:
            case Key.RightShift:
                SetKeyFlag(BoostMask, down);
                break;
        }
    }

    private void SetKeyFlag(int mask, bool enabled)
    {
        SetFlag(ref keyState, mask, enabled);
    }

    private void SetPointerButton(int mask, bool down)
    {
        SetFlag(ref pointerButtons, mask, down);
    }

    private static void SetFlag(ref int target, int mask, bool enabled)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            var next = enabled ? current | mask : current & ~mask;
            if (Interlocked.CompareExchange(ref target, next, current) == current)
                return;
        }
    }
}
