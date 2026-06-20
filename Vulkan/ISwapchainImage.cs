using System;
using System.Threading.Tasks;
using Avalonia;

namespace UniversalUmap.Rendering.Vulkan;

internal interface ISwapchainImage : IAsyncDisposable
{
    PixelSize Size { get; }
    Task? LastPresent { get; }

    void BeginDraw();
    void Present();
}
