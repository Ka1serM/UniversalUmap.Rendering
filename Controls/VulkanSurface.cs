using Avalonia;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Serilog;
using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering.Controls;

internal sealed class VulkanSurface : IAsyncDisposable
{
    private readonly Context context;
    private readonly ICompositionGpuInterop interop;
    private readonly CompositionDrawingSurface target;

    private PixelSize? currentSize;
    private PixelSize? pendingResizeSize;
    private ImageResource? image;
    private SemaphorePair? semaphorePair;
    private ICompositionImportedGpuSemaphore? availableSemaphore;
    private ICompositionImportedGpuSemaphore? renderCompletedSemaphore;
    private ICompositionImportedGpuImage? importedImage;
    private Task? lastPresent;
    private bool initialSubmit = true;
    private Task pendingDisposeTask = Task.CompletedTask;

    public VulkanSurface(Context context, ICompositionGpuInterop interop, CompositionDrawingSurface target)
    {
        this.context = context;
        this.interop = interop;
        this.target = target;
    }

    public bool TryBeginDraw(PixelSize size, out ImageResource drawImage)
    {
        var recreatedImageThisFrame = false;
        if (image is null)
        {
            CreateResources(size);
            recreatedImageThisFrame = true;
        }

        if (currentSize != size)
            pendingResizeSize = size;

        // Coalesce rapid resize events and only recreate backing resources once previous present completed.
        if (pendingResizeSize is { } resizeSize && lastPresent is not { IsCompleted: false })
        {
            // NoorRay-style resize safety: do not swap targets while GPU work is in flight.
            WaitForDeviceIdle();
            QueueDisposeCurrentResources();
            CreateResources(resizeSize);
            pendingResizeSize = null;
            recreatedImageThisFrame = true;
        }

        if (image is null)
            throw new InvalidOperationException("VulkanSurface failed to initialize image resources.");

        // Match NoorRay flow: skip one frame after recreation to settle lifecycle.
        if (recreatedImageThisFrame)
        {
            drawImage = image;
            return false;
        }

        if (currentSize != size)
        {
            drawImage = image;
            return false;
        }

        if (lastPresent is { IsCompleted: false })
        {
            drawImage = image;
            return false;
        }

        drawImage = image;
        BeginDraw();
        return true;
    }

    private void CreateResources(PixelSize size)
    {
        currentSize = size;
        image = new ImageResource(context, (uint)Format.R8G8B8A8Unorm, size, true, interop.SupportedImageHandleTypes);
        semaphorePair = new SemaphorePair(context, true);
        availableSemaphore = null;
        renderCompletedSemaphore = null;
        importedImage = null;
        lastPresent = null;
        initialSubmit = true;
    }

    private void QueueDisposeCurrentResources()
    {
        if (image is null || semaphorePair is null)
            return;

        var imageToDispose = image;
        var semaphorePairToDispose = semaphorePair;
        var availableSemaphoreToDispose = availableSemaphore;
        var renderCompletedSemaphoreToDispose = renderCompletedSemaphore;
        var importedImageToDispose = importedImage;
        var lastPresentToWait = lastPresent;

        image = null;
        semaphorePair = null;
        availableSemaphore = null;
        renderCompletedSemaphore = null;
        importedImage = null;
        lastPresent = null;
        currentSize = null;

        var previousDisposeTask = pendingDisposeTask;
        pendingDisposeTask = DisposeSurfaceResourcesChainAsync(
            previousDisposeTask,
            context,
            imageToDispose,
            semaphorePairToDispose,
            availableSemaphoreToDispose,
            renderCompletedSemaphoreToDispose,
            importedImageToDispose,
            lastPresentToWait);
    }

    private static async Task DisposeSurfaceResourcesChainAsync(
        Task previousDisposeTask,
        Context context,
        ImageResource image,
        SemaphorePair semaphorePair,
        ICompositionImportedGpuSemaphore? availableSemaphore,
        ICompositionImportedGpuSemaphore? renderCompletedSemaphore,
        ICompositionImportedGpuImage? importedImage,
        Task? lastPresent)
    {
        try
        {
            await previousDisposeTask;
            if (lastPresent is not null)
                await lastPresent;

            // Ensure no GPU work can still reference these resources.
            try
            {
                context.Api.DeviceWaitIdle(context.Device).ThrowOnError();
            }
            catch (VulkanException ex) when (ex.Result == Result.ErrorDeviceLost)
            {
                Log.Warning(ex, "Device lost while waiting for idle before disposing Vulkan surface resources.");
            }

            if (importedImage is not null)
                await importedImage.DisposeAsync();
            if (availableSemaphore is not null)
                await availableSemaphore.DisposeAsync();
            if (renderCompletedSemaphore is not null)
                await renderCompletedSemaphore.DisposeAsync();

            semaphorePair.Dispose();
            image.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed disposing Vulkan surface resources.");
        }
    }

    private void BeginDraw()
    {
        if (image is null || semaphorePair is null)
            throw new InvalidOperationException("VulkanSurface resources are unavailable.");

        var commandBuffer = context.CreateCommandBuffer();
        commandBuffer.BeginRecording();
        image.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit);

        if (initialSubmit)
        {
            initialSubmit = false;
            commandBuffer.Submit();
            return;
        }

        commandBuffer.Submit(
            [semaphorePair.ImageAvailableSemaphore],
            [PipelineStageFlags.TransferBit]);
    }

    internal void Present()
    {
        if (image is null || semaphorePair is null || currentSize is null)
            return;

        var commandBuffer = context.CreateCommandBuffer();
        commandBuffer.BeginRecording();
        image.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit);
        commandBuffer.Submit(signalSemaphores: [semaphorePair.RenderFinishedSemaphore]);

        availableSemaphore ??= interop.ImportSemaphore(semaphorePair.Export(false));
        renderCompletedSemaphore ??= interop.ImportSemaphore(semaphorePair.Export(true));
        importedImage ??= interop.ImportImage(
            image.Export(),
            new PlatformGraphicsExternalImageProperties
            {
                Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm,
                Width = currentSize.Value.Width,
                Height = currentSize.Value.Height,
                MemorySize = image.MemorySize
            });

        lastPresent = target.UpdateWithSemaphoresAsync(importedImage, renderCompletedSemaphore, availableSemaphore);
    }

    public async ValueTask DisposeAsync()
    {
        QueueDisposeCurrentResources();
        pendingResizeSize = null;
        await pendingDisposeTask;
    }

    private void WaitForDeviceIdle()
    {
        try
        {
            context.Api.DeviceWaitIdle(context.Device).ThrowOnError();
        }
        catch (VulkanException ex) when (ex.Result == Result.ErrorDeviceLost)
        {
            // Device loss is handled by upper layers; best effort sync only.
            Log.Warning(ex, "Device lost while waiting for idle during Vulkan surface synchronization.");
        }
    }
}
