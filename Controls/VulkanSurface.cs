using System;
using System.Threading.Tasks;
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
    private VulkanSurfaceImage? currentImage;
    private Task pendingDisposeTask = Task.CompletedTask;

    public VulkanSurface(Context context, ICompositionGpuInterop interop, CompositionDrawingSurface target)
    {
        this.context = context;
        this.interop = interop;
        this.target = target;
    }

    public bool TryBeginDraw(PixelSize size, out ImageResource image, out IDisposable? presentScope)
    {
        if (currentImage is null)
        {
            currentImage = new VulkanSurfaceImage(context, size, interop, target);
        }

        if (currentImage.Size != size)
        {
            QueueDispose(currentImage);
            currentImage = new VulkanSurfaceImage(context, size, interop, target);
        }

        if (currentImage.LastPresent is { IsCompleted: false })
        {
            image = currentImage.Image;
            presentScope = null;
            return false;
        }

        image = currentImage.Image;
        currentImage.BeginDraw();
        presentScope = new PresentScope(currentImage);
        return true;
    }

    private void QueueDispose(VulkanSurfaceImage image)
    {
        var previousDisposeTask = pendingDisposeTask;
        pendingDisposeTask = DisposeImageChainAsync(previousDisposeTask, image);
    }

    private static async Task DisposeImageChainAsync(Task previousDisposeTask, VulkanSurfaceImage image)
    {
        try
        {
            await previousDisposeTask;
            await image.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed disposing Vulkan surface image.");
        }
    }

    private sealed class PresentScope : IDisposable
    {
        private readonly VulkanSurfaceImage image;
        private bool disposed;

        public PresentScope(VulkanSurfaceImage image)
        {
            this.image = image;
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            image.Present();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (currentImage is not null)
        {
            QueueDispose(currentImage);
            currentImage = null;
        }

        await pendingDisposeTask;
    }
}

internal sealed class VulkanSurfaceImage : IAsyncDisposable
{
    private readonly Context context;
    private readonly ICompositionGpuInterop interop;
    private readonly CompositionDrawingSurface target;
    private readonly SemaphorePair semaphorePair;
    private readonly ImageResource image;

    private ICompositionImportedGpuSemaphore? availableSemaphore;
    private ICompositionImportedGpuSemaphore? renderCompletedSemaphore;
    private ICompositionImportedGpuImage? importedImage;
    private Task? lastPresent;
    private bool initialSubmit = true;

    public VulkanSurfaceImage(
        Context context,
        PixelSize size,
        ICompositionGpuInterop interop,
        CompositionDrawingSurface target)
    {
        this.context = context;
        this.interop = interop;
        this.target = target;
        Size = size;
        image = new ImageResource(context, (uint)Format.R8G8B8A8Unorm, size, true, interop.SupportedImageHandleTypes);
        semaphorePair = new SemaphorePair(context, true);
    }

    public PixelSize Size { get; }
    public Task? LastPresent => lastPresent;
    public ImageResource Image => image;

    public void BeginDraw()
    {
        var commandBuffer = context.Pool.CreateCommandBuffer();
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

    public void Present()
    {
        var commandBuffer = context.Pool.CreateCommandBuffer();
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
                Width = Size.Width,
                Height = Size.Height,
                MemorySize = image.MemorySize
            });

        lastPresent = target.UpdateWithSemaphoresAsync(importedImage, renderCompletedSemaphore, availableSemaphore);
    }

    public async ValueTask DisposeAsync()
    {
        if (lastPresent is not null)
            await lastPresent;

        if (importedImage is not null)
            await importedImage.DisposeAsync();
        if (availableSemaphore is not null)
            await availableSemaphore.DisposeAsync();
        if (renderCompletedSemaphore is not null)
            await renderCompletedSemaphore.DisposeAsync();

        semaphorePair.Dispose();
        image.Dispose();
    }
}
