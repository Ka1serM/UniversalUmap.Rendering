using System.Collections.Generic;
using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Serilog;

namespace UniversalUmap.Rendering;

internal sealed class Input
{
    private readonly object sync = new();
    private readonly HashSet<Key> pressedKeys = [];
    private bool rightMouseDown;
    private Point lastPointerPosition;
    private bool hasPointerPosition;
    private Vector2 pendingMouseDelta;
    private float pendingWheelDelta;

    public void OnPointerPressed(Point position, bool rightButton)
    {
        lock (sync)
        {
            if (rightButton)
                rightMouseDown = true;
            lastPointerPosition = position;
            hasPointerPosition = true;
            pendingMouseDelta = Vector2.Zero;
        }
        Log.Information("Input pointer pressed. RightButton={RightButton} Pos=({X},{Y})", rightButton, position.X, position.Y);
    }

    public void OnPointerReleased(bool rightButton)
    {
        if (!rightButton)
            return;

        lock (sync)
        {
            rightMouseDown = false;
            hasPointerPosition = false;
            pendingMouseDelta = Vector2.Zero;
        }
        Log.Information("Input pointer released. RightButton={RightButton}", rightButton);
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
        Log.Information("Input wheel delta={Delta}", deltaY);
    }

    public void OnKeyDown(Key key)
    {
        lock (sync)
            pressedKeys.Add(key);
        Log.Information("Input key down: {Key}", key);
    }

    public void OnKeyUp(Key key)
    {
        lock (sync)
            pressedKeys.Remove(key);
        Log.Information("Input key up: {Key}", key);
    }

    public void OnFocusLost()
    {
        lock (sync)
        {
            rightMouseDown = false;
            hasPointerPosition = false;
            pendingMouseDelta = Vector2.Zero;
            pendingWheelDelta = 0f;
            pressedKeys.Clear();
        }
        Log.Information("Input focus lost; transient state cleared.");
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
}
