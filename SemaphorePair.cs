using System;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace UniversalUmap.Rendering;

public sealed class SemaphorePair : IDisposable
{
    private readonly Context context;

    public Silk.NET.Vulkan.Semaphore ImageAvailableSemaphore { get; }
    public Silk.NET.Vulkan.Semaphore RenderFinishedSemaphore { get; }

    public unsafe SemaphorePair(Context context, bool exportable)
    {
        this.context = context;

        var exportInfo = new ExportSemaphoreCreateInfo
        {
            SType = StructureType.ExportSemaphoreCreateInfo,
            HandleTypes = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ExternalSemaphoreHandleTypeFlags.OpaqueWin32Bit
                : ExternalSemaphoreHandleTypeFlags.OpaqueFDBit
        };

        var createInfo = new SemaphoreCreateInfo
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = exportable ? &exportInfo : default
        };

        context.Api.CreateSemaphore(context.Device, in createInfo, default, out var semaphore).ThrowOnError();
        ImageAvailableSemaphore = semaphore;
        context.Api.CreateSemaphore(context.Device, in createInfo, default, out semaphore).ThrowOnError();
        RenderFinishedSemaphore = semaphore;
    }

    public unsafe IntPtr ExportWin32(bool renderFinished)
    {
        if (!context.Api.TryGetDeviceExtension<KhrExternalSemaphoreWin32>(context.Instance, context.Device, out var ext))
            throw new InvalidOperationException("VK_KHR_external_semaphore_win32 unavailable");

        var info = new SemaphoreGetWin32HandleInfoKHR
        {
            SType = StructureType.SemaphoreGetWin32HandleInfoKhr,
            Semaphore = renderFinished ? RenderFinishedSemaphore : ImageAvailableSemaphore,
            HandleType = ExternalSemaphoreHandleTypeFlags.OpaqueWin32Bit
        };
        ext.GetSemaphoreWin32Handle(context.Device, in info, out var handle).ThrowOnError();
        return handle;
    }

    public unsafe int ExportFd(bool renderFinished)
    {
        if (!context.Api.TryGetDeviceExtension<KhrExternalSemaphoreFd>(context.Instance, context.Device, out var ext))
            throw new InvalidOperationException("VK_KHR_external_semaphore_fd unavailable");

        var info = new SemaphoreGetFdInfoKHR
        {
            SType = StructureType.SemaphoreGetFDInfoKhr,
            Semaphore = renderFinished ? RenderFinishedSemaphore : ImageAvailableSemaphore,
            HandleType = ExternalSemaphoreHandleTypeFlags.OpaqueFDBit
        };
        ext.GetSemaphoreF(context.Device, in info, out var fd).ThrowOnError();
        return fd;
    }

    public IPlatformHandle Export(bool renderFinished)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new PlatformHandle(
                ExportWin32(renderFinished),
                KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaqueNtHandle);
        }

        return new PlatformHandle(
            new IntPtr(ExportFd(renderFinished)),
            KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaquePosixFileDescriptor);
    }

    public unsafe void Dispose()
    {
        context.Api.DestroySemaphore(context.Device, ImageAvailableSemaphore, default);
        context.Api.DestroySemaphore(context.Device, RenderFinishedSemaphore, default);
    }
}
