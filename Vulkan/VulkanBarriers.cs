using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering.Vulkan;

internal static unsafe class VulkanBarriers
{
    public static void Memory(
        Vk api,
        CommandBuffer commandBuffer,
        AccessFlags sourceAccessMask,
        AccessFlags destinationAccessMask,
        PipelineStageFlags sourceStageMask = PipelineStageFlags.AllCommandsBit,
        PipelineStageFlags destinationStageMask = PipelineStageFlags.AllCommandsBit)
    {
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = sourceAccessMask,
            DstAccessMask = destinationAccessMask
        };

        api.CmdPipelineBarrier(
            commandBuffer,
            sourceStageMask,
            destinationStageMask,
            0,
            1,
            in barrier,
            0,
            null,
            0,
            null);
    }

    public static void Image(
        Vk api,
        CommandBuffer commandBuffer,
        Image image,
        ImageAspectFlags aspectMask,
        ImageLayout sourceLayout,
        AccessFlags sourceAccessMask,
        ImageLayout destinationLayout,
        AccessFlags destinationAccessMask,
        uint baseMipLevel = 0,
        uint mipLevels = 1,
        uint baseArrayLayer = 0,
        uint layerCount = 1,
        PipelineStageFlags sourceStageMask = PipelineStageFlags.AllCommandsBit,
        PipelineStageFlags destinationStageMask = PipelineStageFlags.AllCommandsBit)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = sourceAccessMask,
            DstAccessMask = destinationAccessMask,
            OldLayout = sourceLayout,
            NewLayout = destinationLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(aspectMask, baseMipLevel, mipLevels, baseArrayLayer, layerCount)
        };

        api.CmdPipelineBarrier(
            commandBuffer,
            sourceStageMask,
            destinationStageMask,
            0,
            0,
            null,
            0,
            null,
            1,
            in barrier);
    }

    public static void Buffer(
        Vk api,
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer buffer,
        AccessFlags sourceAccessMask,
        AccessFlags destinationAccessMask,
        ulong offset = 0,
        ulong size = ulong.MaxValue,
        PipelineStageFlags sourceStageMask = PipelineStageFlags.ComputeShaderBit,
        PipelineStageFlags destinationStageMask = PipelineStageFlags.ComputeShaderBit)
    {
        var barrier = new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = sourceAccessMask,
            DstAccessMask = destinationAccessMask,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = buffer,
            Offset = offset,
            Size = size
        };

        api.CmdPipelineBarrier(
            commandBuffer,
            sourceStageMask,
            destinationStageMask,
            0,
            0,
            null,
            1,
            in barrier,
            0,
            null);
    }
}
