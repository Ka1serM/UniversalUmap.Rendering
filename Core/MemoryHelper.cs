using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Vulkan;

namespace UniversalUmap.Rendering.Core;

internal static class MemoryHelper
{
    internal static int FindSuitableMemoryTypeIndex(
        Vk api,
        PhysicalDevice physicalDevice,
        uint memoryTypeBits,
        MemoryPropertyFlags flags)
    {
        api.GetPhysicalDeviceMemoryProperties(physicalDevice, out var properties);
        for (var i = 0; i < properties.MemoryTypeCount; i++)
        {
            var type = properties.MemoryTypes[i];
            if ((memoryTypeBits & (1u << i)) != 0 && type.PropertyFlags.HasFlag(flags))
                return i;
        }

        return -1;
    }

    internal static unsafe void TransitionLayout(
        Vk api,
        CommandBuffer commandBuffer,
        Image image,
        ImageLayout sourceLayout,
        AccessFlags sourceAccessMask,
        ImageLayout destinationLayout,
        AccessFlags destinationAccessMask,
        uint mipLevels)
    {
        VulkanBarriers.Image(
            api,
            commandBuffer,
            image,
            ImageAspectFlags.ColorBit,
            sourceLayout,
            sourceAccessMask,
            destinationLayout,
            destinationAccessMask,
            mipLevels: mipLevels);
    }
}
