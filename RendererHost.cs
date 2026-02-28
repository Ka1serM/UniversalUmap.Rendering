using System;

namespace UniversalUmap.Rendering;

public static class RendererHost
{
    private static readonly object Sync = new();
    private static WeakReference<Renderer>? current;

    internal static void Register(Renderer renderer)
    {
        lock (Sync)
            current = new WeakReference<Renderer>(renderer);
    }

    internal static void Unregister(Renderer renderer)
    {
        lock (Sync)
        {
            if (current is not null && current.TryGetTarget(out var target) && ReferenceEquals(target, renderer))
                current = null;
        }
    }

    public static bool TryGetRenderer(out Renderer? renderer)
    {
        lock (Sync)
        {
            if (current is not null && current.TryGetTarget(out var target))
            {
                renderer = target;
                return true;
            }
        }

        renderer = null;
        return false;
    }
}
