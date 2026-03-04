using System;
using System.Threading.Tasks;
using Avalonia.Rendering.Composition;

namespace UniversalUmap.Rendering;

internal static class SharedRendererContext
{
    private static readonly object Sync = new();
    private static Renderer? renderer;
    private static int leaseCount;

    internal sealed class Lease : IDisposable
    {
        public Renderer Renderer { get; }
        private bool disposed;

        public Lease(Renderer renderer)
        {
            Renderer = renderer;
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            Release();
        }
    }

    public static async Task<Lease?> AcquireAsync(Compositor compositor)
    {
        lock (Sync)
        {
            if (renderer is not null)
            {
                leaseCount++;
                return new Lease(renderer);
            }
        }

        var interop = await compositor.TryGetCompositionGpuInterop();
        if (interop is null)
            return null;

        lock (Sync)
        {
            if (renderer is null)
            {
                renderer = new Renderer(interop);
                RendererHost.Register(renderer);
            }

            leaseCount++;
            return new Lease(renderer);
        }
    }

    public static void Reset()
    {
        Renderer? toDispose = null;
        lock (Sync)
        {
            if (renderer is null)
                return;

            toDispose = renderer;
            renderer = null;
            leaseCount = 0;
        }

        RendererHost.Unregister(toDispose);
        toDispose.Dispose();
    }

    private static void Release()
    {
        lock (Sync)
        {
            if (leaseCount > 0)
                leaseCount--;
        }
    }
}
