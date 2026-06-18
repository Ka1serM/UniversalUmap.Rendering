using System.Numerics;

namespace UniversalUmap.Rendering.Vulkan;

internal interface IGpuRenderPath : IDisposable
{
    int ShaderPixelSizePercent { get; set; }
    VulkanImage OutputColor { get; }
    VulkanImage OutputAlbedo { get; }
    VulkanImage OutputNormal { get; }
    VulkanImage OutputCrypto { get; }
    VulkanImage OutputPosition { get; }
    VulkanImage OutputAdaptiveState { get; }
    Avalonia.PixelSize RenderImageSize { get; }
    void Record(Avalonia.PixelSize renderSize, VulkanImage image, Context.CommandBuffer commandBuffer, Scene.RenderDataGpu renderData);
    bool QueryPixelUInt(VulkanImage image, int pixelX, int pixelY, out uint value);
    bool QueryPixelHalf4(VulkanImage image, int pixelX, int pixelY, out Vector4 value);
}
