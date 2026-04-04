using System;
using Avalonia;
using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering.Vulkan;

internal sealed class OffscreenPresentationBuffer : IDisposable
{
    private readonly Context context;
    private VulkanImage? colorImage;
    private PixelSize size;

    public OffscreenPresentationBuffer(Context context)
    {
        this.context = context;
    }

    public VulkanImage ColorImage => colorImage ?? throw new InvalidOperationException("Offscreen presentation buffer is not initialized.");

    public void EnsureSize(PixelSize desiredSize)
    {
        if (desiredSize.Width <= 0 || desiredSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(desiredSize));

        if (colorImage is not null && size == desiredSize)
            return;

        colorImage?.Dispose();
        colorImage = new VulkanImage(context, (uint)Format.R8G8B8A8Unorm, desiredSize, false, Array.Empty<string>());
        size = desiredSize;
    }

    public void BlitToPresentedImage(Context.CommandBuffer commandBuffer, VulkanImage presentedImage)
    {
        var sourceImage = ColorImage;
        sourceImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit);
        presentedImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit);

        var region = new ImageBlit
        {
            SrcOffsets = new ImageBlit.SrcOffsetsBuffer
            {
                Element0 = new Offset3D(0, 0, 0),
                Element1 = new Offset3D(sourceImage.Size.Width, sourceImage.Size.Height, 1),
            },
            DstOffsets = new ImageBlit.DstOffsetsBuffer
            {
                Element0 = new Offset3D(0, 0, 0),
                Element1 = new Offset3D(presentedImage.Size.Width, presentedImage.Size.Height, 1),
            },
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseArrayLayer = 0,
                LayerCount = 1,
                MipLevel = 0
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseArrayLayer = 0,
                LayerCount = 1,
                MipLevel = 0
            }
        };

        context.Api.CmdBlitImage(
            commandBuffer.InternalHandle,
            sourceImage.InternalHandle,
            ImageLayout.TransferSrcOptimal,
            presentedImage.InternalHandle,
            ImageLayout.TransferDstOptimal,
            1,
            in region,
            Filter.Linear);

        presentedImage.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit);
    }

    public void Dispose()
    {
        colorImage?.Dispose();
        colorImage = null;
        size = default;
    }
}
