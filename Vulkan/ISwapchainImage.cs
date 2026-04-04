using System;
using System.Threading.Tasks;
using Avalonia;

namespace UniversalUmap.Rendering.Vulkan;

/// <summary>
/// Minimal interface for swapchain images.
/// This is a local copy to avoid depending on internal Avalonia APIs.
/// </summary>
internal interface ISwapchainImage : IAsyncDisposable
{
    PixelSize Size { get; }
    Task? LastPresent { get; }

    void BeginDraw();
    void Present();
}
