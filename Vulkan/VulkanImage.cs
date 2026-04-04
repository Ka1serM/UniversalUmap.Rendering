using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using UniversalUmap.Rendering.Core;

namespace UniversalUmap.Rendering.Vulkan;

public sealed unsafe class VulkanImage : IDisposable
{
    private readonly Context context;
    private readonly ImageUsageFlags imageUsageFlags;
    private readonly ImageView imageView;
    private readonly DeviceMemory imageMemory;
    private ImageLayout currentLayout;
    private AccessFlags currentAccessFlags;

    internal Image InternalHandle { get; }
    internal ImageView InternalView => imageView;
    internal DeviceMemory InternalMemory => imageMemory;
    public PixelSize Size { get; }
    public uint MipLevels { get; }
    public ulong MemorySize { get; }
    public ulong Handle => InternalHandle.Handle;
    public ulong ViewHandle => imageView.Handle;
    public ulong MemoryHandle => imageMemory.Handle;
    public uint UsageFlags => (uint)imageUsageFlags;
    public uint CurrentLayout => (uint)currentLayout;

    public VulkanImage(
        Context context,
        uint format,
        PixelSize size,
        bool exportable,
        IReadOnlyList<string> supportedHandleTypes,
        uint mipLevels = 1)
    {
        this.context = context;
        Size = size;
        MipLevels = Math.Max(1u, mipLevels);
        imageUsageFlags = ImageUsageFlags.ColorAttachmentBit
            | ImageUsageFlags.TransferDstBit
            | ImageUsageFlags.TransferSrcBit
            | ImageUsageFlags.SampledBit
            | ImageUsageFlags.StorageBit;

        var handleType = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ExternalMemoryHandleTypeFlags.OpaqueWin32Bit
            : ExternalMemoryHandleTypeFlags.OpaqueFDBit;

        if (exportable)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                && !supportedHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle))
            {
                throw new NotSupportedException("Vulkan Opaque NT export is not supported by compositor");
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                && !supportedHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor))
            {
                throw new NotSupportedException("Vulkan Opaque FD export is not supported by compositor");
            }
        }

        var externalImageInfo = new ExternalMemoryImageCreateInfo
        {
            SType = StructureType.ExternalMemoryImageCreateInfo,
            HandleTypes = handleType
        };

        var imageInfo = new ImageCreateInfo
        {
            PNext = exportable ? &externalImageInfo : default,
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = (Format)format,
            Extent = new Extent3D((uint?)Size.Width, (uint?)Size.Height, 1),
            MipLevels = MipLevels,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = imageUsageFlags,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
            Flags = ImageCreateFlags.CreateMutableFormatBit
        };

        context.Api.CreateImage(context.Device, in imageInfo, default, out var image).ThrowOnError();
        InternalHandle = image;

        context.Api.GetImageMemoryRequirements(context.Device, InternalHandle, out var memoryRequirements);
        MemorySize = memoryRequirements.Size;

        var dedicated = new MemoryDedicatedAllocateInfoKHR
        {
            SType = StructureType.MemoryDedicatedAllocateInfoKhr,
            Image = image
        };
        var exportAllocate = new ExportMemoryAllocateInfo
        {
            SType = StructureType.ExportMemoryAllocateInfo,
            HandleTypes = handleType,
            PNext = &dedicated
        };
        var allocInfo = new MemoryAllocateInfo
        {
            PNext = exportable ? &exportAllocate : default,
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memoryRequirements.Size,
            MemoryTypeIndex = (uint)MemoryHelper.FindSuitableMemoryTypeIndex(
                context.Api,
                context.PhysicalDevice,
                memoryRequirements.MemoryTypeBits,
                MemoryPropertyFlags.DeviceLocalBit)
        };

        context.Api.AllocateMemory(context.Device, in allocInfo, default, out imageMemory).ThrowOnError();
        context.Api.BindImageMemory(context.Device, InternalHandle, imageMemory, 0).ThrowOnError();

        var subresource = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, MipLevels, 0, 1);
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = InternalHandle,
            ViewType = ImageViewType.Type2D,
            Format = (Format)format,
            Components = new ComponentMapping(
                ComponentSwizzle.Identity,
                ComponentSwizzle.Identity,
                ComponentSwizzle.Identity,
                ComponentSwizzle.Identity),
            SubresourceRange = subresource
        };

        context.Api.CreateImageView(context.Device, in viewInfo, default, out imageView).ThrowOnError();
        currentLayout = ImageLayout.Undefined;
        currentAccessFlags = AccessFlags.None;
    }

    public unsafe int ExportFd()
    {
        if (!context.Api.TryGetDeviceExtension<KhrExternalMemoryFd>(context.Instance, context.Device, out var ext))
            throw new InvalidOperationException("VK_KHR_external_memory_fd unavailable");

        var info = new MemoryGetFdInfoKHR
        {
            SType = StructureType.MemoryGetFDInfoKhr,
            Memory = imageMemory,
            HandleType = ExternalMemoryHandleTypeFlags.OpaqueFDBit
        };
        ext.GetMemoryF(context.Device, in info, out var fd).ThrowOnError();
        return fd;
    }

    public unsafe IntPtr ExportNt()
    {
        if (!context.Api.TryGetDeviceExtension<KhrExternalMemoryWin32>(context.Instance, context.Device, out var ext))
            throw new InvalidOperationException("VK_KHR_external_memory_win32 unavailable");

        var info = new MemoryGetWin32HandleInfoKHR
        {
            SType = StructureType.MemoryGetWin32HandleInfoKhr,
            Memory = imageMemory,
            HandleType = ExternalMemoryHandleTypeFlags.OpaqueWin32Bit
        };
        ext.GetMemoryWin32Handle(context.Device, in info, out var handle).ThrowOnError();
        return handle;
    }

    public IPlatformHandle Export()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new PlatformHandle(
                ExportNt(),
                KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle);
        }

        return new PlatformHandle(
            new IntPtr(ExportFd()),
            KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor);
    }

    public void TransitionLayout(
        CommandBuffer commandBuffer,
        ImageLayout destinationLayout,
        AccessFlags destinationAccessFlags)
    {
        MemoryHelper.TransitionLayout(
            context.Api,
            commandBuffer,
            InternalHandle,
            currentLayout,
            currentAccessFlags,
            destinationLayout,
            destinationAccessFlags,
            MipLevels);

        currentLayout = destinationLayout;
        currentAccessFlags = destinationAccessFlags;
    }

    public void SetTrackedLayout(ImageLayout layout, AccessFlags accessFlags)
    {
        currentLayout = layout;
        currentAccessFlags = accessFlags;
    }

    public unsafe void Dispose()
    {
        context.Api.DestroyImageView(context.Device, imageView, default);
        context.Api.DestroyImage(context.Device, InternalHandle, default);
        context.Api.FreeMemory(context.Device, imageMemory, default);
    }
}
