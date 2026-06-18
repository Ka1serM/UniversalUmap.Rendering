using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using UniversalUmap.Rendering.Core;

#if OS_WINDOWS
using SharpDX.Direct3D11;
using VorticeDXGI = SharpDX.DXGI;
#endif

namespace UniversalUmap.Rendering.Vulkan;

public sealed unsafe class VulkanImage : IDisposable
{
    private readonly Context context;
    private readonly ImageUsageFlags imageUsageFlags;
    private readonly ImageView imageView;
    private readonly DeviceMemory imageMemory;
    private ImageLayout currentLayout;
    private AccessFlags currentAccessFlags;
#if OS_WINDOWS
    private readonly Texture2D? d3dTexture2D;
#endif

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

        // Determine handle type following GpuInterop sample pattern
        var handleType = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (supportedHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle)
               && !supportedHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle)
                ? ExternalMemoryHandleTypeFlags.D3D11TextureBit
                : ExternalMemoryHandleTypeFlags.OpaqueWin32Bit)
            : ExternalMemoryHandleTypeFlags.OpaqueFDBit;

        if (exportable)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var supportsVulkanNt = supportedHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle);
                var supportsD3D11 = supportedHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle);
                
                if (handleType == ExternalMemoryHandleTypeFlags.D3D11TextureBit && !supportsD3D11)
                {
                    throw new NotSupportedException("D3D11 Texture NT export is not supported by compositor");
                }
                
                if (handleType == ExternalMemoryHandleTypeFlags.OpaqueWin32Bit && !supportsVulkanNt)
                {
                    throw new NotSupportedException("Vulkan Opaque NT export is not supported by compositor");
                }
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

#if OS_WINDOWS
        ImportMemoryWin32HandleInfoKHR handleImport = default;
        if (handleType == ExternalMemoryHandleTypeFlags.D3D11TextureBit && exportable)
        {
            var d3dDevice = context.D3DDevice ?? throw new NotSupportedException("Vulkan D3DDevice wasn't created");
            d3dTexture2D = D3DMemoryHelper.CreateMemoryHandle(d3dDevice, size, (Format)format);
            using var dxgi = d3dTexture2D.QueryInterface<SharpDX.DXGI.Resource1>();

            handleImport = new ImportMemoryWin32HandleInfoKHR
            {
                PNext = &dedicated,
                SType = StructureType.ImportMemoryWin32HandleInfoKhr,
                HandleType = ExternalMemoryHandleTypeFlags.D3D11TextureBit,
                Handle = dxgi.CreateSharedHandle(null, VorticeDXGI.SharedResourceFlags.Read | VorticeDXGI.SharedResourceFlags.Write),
            };
        }
#endif

        var exportAllocate = new ExportMemoryAllocateInfo
        {
            SType = StructureType.ExportMemoryAllocateInfo,
            HandleTypes = handleType,
            PNext = &dedicated
        };
        var allocInfo = new MemoryAllocateInfo
        {
            PNext = exportable
#if OS_WINDOWS
                ? handleImport.Handle != IntPtr.Zero ? &handleImport : &exportAllocate
#else
                ? &exportAllocate
#endif
                : default,
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
#if OS_WINDOWS
            if (d3dTexture2D != null)
            {
                using var dxgi = d3dTexture2D.QueryInterface<SharpDX.DXGI.Resource1>();
                return new PlatformHandle(
                    dxgi.CreateSharedHandle(null, VorticeDXGI.SharedResourceFlags.Read | VorticeDXGI.SharedResourceFlags.Write),
                    KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle);
            }
#endif

            return new PlatformHandle(ExportNt(),
                KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle);
        }

        return new PlatformHandle(
            new IntPtr(ExportFd()),
            KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor);
    }

    public bool IsDirectXBacked
    {
        get
        {
#if OS_WINDOWS
            return d3dTexture2D != null;
#else
            return false;
#endif
        }
    }

    public void TransitionLayout(
        CommandBuffer commandBuffer,
        ImageLayout destinationLayout,
        AccessFlags destinationAccessFlags)
    {
        if (currentLayout == destinationLayout && currentAccessFlags == destinationAccessFlags)
            return;

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
#if OS_WINDOWS
        d3dTexture2D?.Dispose();
#endif
    }
}
