using Avalonia;
using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Core;

namespace UniversalUmap.Rendering.Vulkan;

internal sealed unsafe class VulkanDepthImage : IDisposable
{
    private readonly Context context;
    private readonly ImageView imageView;
    private readonly DeviceMemory imageMemory;
    private ImageLayout currentLayout;
    private AccessFlags currentAccessFlags;

    internal Image Handle { get; }
    internal ImageView View => imageView;
    public PixelSize Size { get; }

    public VulkanDepthImage(Context context, Format format, PixelSize size)
    {
        this.context = context;
        Size = size;

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D((uint)Math.Max(1, size.Width), (uint)Math.Max(1, size.Height), 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };

        context.Api.CreateImage(context.Device, in imageInfo, default, out var image).ThrowOnError();
        Handle = image;

        context.Api.GetImageMemoryRequirements(context.Device, Handle, out var memoryRequirements);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memoryRequirements.Size,
            MemoryTypeIndex = (uint)MemoryHelper.FindSuitableMemoryTypeIndex(
                context.Api,
                context.PhysicalDevice,
                memoryRequirements.MemoryTypeBits,
                MemoryPropertyFlags.DeviceLocalBit)
        };

        context.Api.AllocateMemory(context.Device, in allocInfo, default, out imageMemory).ThrowOnError();
        context.Api.BindImageMemory(context.Device, Handle, imageMemory, 0).ThrowOnError();

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Handle,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1)
        };

        context.Api.CreateImageView(context.Device, in viewInfo, default, out imageView).ThrowOnError();
        currentLayout = ImageLayout.Undefined;
        currentAccessFlags = AccessFlags.None;
    }

    public void TransitionLayout(CommandBuffer commandBuffer, ImageLayout destinationLayout, AccessFlags destinationAccessFlags)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = currentAccessFlags,
            DstAccessMask = destinationAccessFlags,
            OldLayout = currentLayout,
            NewLayout = destinationLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = Handle,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1)
        };

        context.Api.CmdPipelineBarrier(
            commandBuffer,
            PipelineStageFlags.AllCommandsBit,
            PipelineStageFlags.AllCommandsBit,
            0,
            0,
            null,
            0,
            null,
            1,
            in barrier);

        currentLayout = destinationLayout;
        currentAccessFlags = destinationAccessFlags;
    }

    public void Dispose()
    {
        context.Api.DestroyImageView(context.Device, imageView, default);
        context.Api.DestroyImage(context.Device, Handle, default);
        context.Api.FreeMemory(context.Device, imageMemory, default);
    }
}
