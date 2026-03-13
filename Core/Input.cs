using System.Collections.Generic;
using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Serilog;

namespace UniversalUmap.Rendering.Core;

internal sealed class Input
{
    private readonly object sync = new();
    private readonly HashSet<Key> pressedKeys = [];
    private bool leftMouseDown;
    private bool rightMouseDown;
    private Point lastPointerPosition;
    private bool hasPointerPosition;
    private Vector2 pendingMouseDelta;
    private float pendingWheelDelta;

    public void OnPointerPressed(Point position, bool leftButton, bool rightButton)
    {
        lock (sync)
        {
            if (leftButton)
                leftMouseDown = true;
            if (rightButton)
                rightMouseDown = true;
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

        lock (sync)
        {
            if (leftButton)
                leftMouseDown = false;
            if (rightButton)
                rightMouseDown = false;
            if (!leftMouseDown && !rightMouseDown)
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
        lock (sync)
            pressedKeys.Add(key);
        Log.Debug("Input key down: {Key}", key);
    }

    public void OnKeyUp(Key key)
    {
        lock (sync)
            pressedKeys.Remove(key);
        Log.Debug("Input key up: {Key}", key);
    }

    public void OnFocusLost()
    {
        lock (sync)
        {
            leftMouseDown = false;
            rightMouseDown = false;
            hasPointerPosition = false;
            pendingMouseDelta = Vector2.Zero;
            pendingWheelDelta = 0f;
            pressedKeys.Clear();
        }
        Log.Debug("Input focus lost; transient state cleared.");
    }

    public void SetModifierState(KeyModifiers modifiers)
    {
        lock (sync)
        {
            SetPairedKeyState((modifiers & KeyModifiers.Alt) != 0, Key.LeftAlt, Key.RightAlt);
            SetPairedKeyState((modifiers & KeyModifiers.Shift) != 0, Key.LeftShift, Key.RightShift);
            SetPairedKeyState((modifiers & KeyModifiers.Control) != 0, Key.LeftCtrl, Key.RightCtrl);
        }
    }

    public bool IsKeyDown(Key key)
    {
        lock (sync)
        {
            return pressedKeys.Contains(key);
        }
    }

    public bool RightMouseDown
    {
        get
        {
            lock (sync)
                return rightMouseDown;
        }
    }

    public bool LeftMouseDown
    {
        get
        {
            lock (sync)
                return leftMouseDown;
        }
    }

    public bool TryGetPointerPosition(out Point position)
    {
        lock (sync)
        {
            if (!hasPointerPosition)
            {
                position = default;
                return false;
            }

            position = lastPointerPosition;
            return true;
        }
    }

    public Vector2 ConsumeMouseDelta()
    {
        lock (sync)
        {
            var mouseDelta = pendingMouseDelta;
            pendingMouseDelta = Vector2.Zero;
            return mouseDelta;
        }
    }

    public float ConsumeWheelDelta()
    {
        lock (sync)
        {
            var wheelDelta = pendingWheelDelta;
            pendingWheelDelta = 0f;
            return wheelDelta;
        }
    }

    private void SetPairedKeyState(bool down, Key a, Key b)
    {
        if (down)
        {
            pressedKeys.Add(a);
            pressedKeys.Add(b);
            return;
        }

        pressedKeys.Remove(a);
        pressedKeys.Remove(b);
    }
}
