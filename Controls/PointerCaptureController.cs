using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace UniversalUmap.Rendering.Controls;

internal sealed class PointerCaptureController
{
    private static readonly Cursor HiddenCursor = new(StandardCursorType.None);
    private static readonly Cursor DefaultCursor = new(StandardCursorType.Arrow);

    private bool active;
    private bool suppressWarpMove;
    private Point captureCenterLocal;

    public bool IsActive => active;

    public bool Begin(Control control, IPointer pointer)
    {
        if (active)
            return false;

        pointer.Capture(control);
        control.Cursor = HiddenCursor;
        active = true;
        captureCenterLocal = new Point(control.Bounds.Width * 0.5, control.Bounds.Height * 0.5);
        if (TryWarpPointerToCaptureCenter(control))
            suppressWarpMove = true;
        return true;
    }

    public void End(Control control, IPointer? pointer = null)
    {
        if (!active)
            return;

        active = false;
        suppressWarpMove = false;
        control.Cursor = DefaultCursor;
        pointer?.Capture(null);
    }

    public bool TryConsumeWarpSuppressedMove()
    {
        if (!suppressWarpMove)
            return false;

        suppressWarpMove = false;
        return true;
    }

    public Vector GetDeltaFromCenter(Point pointerPosition)
    {
        return new Vector(pointerPosition.X - captureCenterLocal.X, pointerPosition.Y - captureCenterLocal.Y);
    }

    public bool Recenter(Control control)
    {
        if (!active)
            return false;

        if (TryWarpPointerToCaptureCenter(control))
        {
            suppressWarpMove = true;
            return true;
        }

        return false;
    }

    private bool TryWarpPointerToCaptureCenter(Control control)
    {
        if (!OperatingSystem.IsLinux())
            return false;

        var screenPoint = control.PointToScreen(captureCenterLocal);
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
