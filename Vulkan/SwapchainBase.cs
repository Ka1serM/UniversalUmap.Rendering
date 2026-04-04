using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;

namespace UniversalUmap.Rendering.Vulkan;

/// <summary>
/// Minimal swapchain base class for image pool management.
/// This is a local copy to avoid depending on internal Avalonia APIs.
/// </summary>
internal abstract class SwapchainBase<TImage> : IAsyncDisposable where TImage : class, ISwapchainImage
{
    protected ICompositionGpuInterop Interop { get; }
    protected CompositionDrawingSurface Target { get; }
    private readonly List<TImage> _pendingImages = new();

    public SwapchainBase(ICompositionGpuInterop interop, CompositionDrawingSurface target)
    {
        Interop = interop;
        Target = target;
    }

    private static bool IsBroken(TImage image) => image.LastPresent?.IsFaulted == true;
    private static bool IsReady(TImage image) => image.LastPresent == null || image.LastPresent.Status == TaskStatus.RanToCompletion;

    private TImage? CleanupAndFindNextImage(PixelSize size)
    {
        TImage? firstFound = null;
        var foundMultiple = false;

        for (var c = _pendingImages.Count - 1; c >= 0; c--)
        {
            var image = _pendingImages[c];
            var ready = IsReady(image);
            var matches = image.Size == size;

            if (IsBroken(image) || (!matches && ready))
            {
                image.DisposeAsync();
                _pendingImages.RemoveAt(c);
            }

            if (matches && ready)
            {
                if (firstFound == null)
                    firstFound = image;
                else
                    foundMultiple = true;
            }
        }

        return foundMultiple ? firstFound : null;
    }

    protected abstract TImage CreateImage(PixelSize size);

    protected IDisposable BeginDrawCore(PixelSize size, out TImage image)
    {
        var img = CleanupAndFindNextImage(size) ?? CreateImage(size);

        img.BeginDraw();
        _pendingImages.Remove(img);
        image = img;

        return new DisposableHelper(() =>
        {
            img.Present();
            _pendingImages.Add(img);
        });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var img in _pendingImages)
            await img.DisposeAsync();
        _pendingImages.Clear();
    }

    private class DisposableHelper : IDisposable
    {
        private readonly Action _onDispose;
        public DisposableHelper(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose?.Invoke();
    }
}
