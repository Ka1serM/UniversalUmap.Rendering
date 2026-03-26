using System.Numerics;

namespace UniversalUmap.Rendering.Vulkan;

internal interface IGpuRenderPath : IDisposable
{
    int ShaderPixelSizePercent { get; set; }
    ImageResource OutputColor { get; }
    ImageResource OutputAlbedo { get; }
    ImageResource OutputNormal { get; }
    ImageResource OutputCrypto { get; }
    ImageResource OutputPosition { get; }
    ImageResource OutputAdaptiveState { get; }
    Avalonia.PixelSize RenderImageSize { get; }
    bool PickBuffersFlippedY { get; }

    void Record(Avalonia.PixelSize renderSize, ImageResource image, Context.CommandBuffer commandBuffer, Scene.RenderDataGpu renderData);
    bool QueryPixelUInt(ImageResource image, int pixelX, int pixelY, out uint value);
    bool QueryPixelHalf4(ImageResource image, int pixelX, int pixelY, out Vector4 value);
}
