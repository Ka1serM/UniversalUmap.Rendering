using Avalonia;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Serilog;
using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Core;
using UniversalUmap.Rendering.Vulkan;

namespace UniversalUmap.Rendering.Controls;

internal sealed class VulkanSurface : IAsyncDisposable
{
    private sealed class SurfaceGeneration
    {
        public required PixelSize Size { get; init; }
        public required ImageResource Image { get; init; }
        public required SemaphorePair SemaphorePair { get; init; }

        public ICompositionImportedGpuSemaphore? AvailableSemaphore { get; set; }
        public ICompositionImportedGpuSemaphore? RenderCompletedSemaphore { get; set; }
        public ICompositionImportedGpuImage? ImportedImage { get; set; }
        public Task? LastPresent { get; set; }
        public Context.CommandBuffer? LastSubmission { get; set; }
        public bool InitialSubmit { get; set; } = true;

        public void DisposeOwnedResources()
        {
            SemaphorePair.Dispose();
            Image.Dispose();
        }
    }

    private readonly Context context;
    private readonly ICompositionGpuInterop interop;
    private readonly CompositionDrawingSurface target;

    private SurfaceGeneration? currentGeneration;
    private Task pendingDisposeTask = Task.CompletedTask;

    public VulkanSurface(Context context, ICompositionGpuInterop interop, CompositionDrawingSurface target)
    {
        this.context = context;
        this.interop = interop;
        this.target = target;
    }

    public bool TryBeginDraw(PixelSize size, out ImageResource drawImage)
    {
        var generation = GetOrCreateCurrentGeneration(size);
        if (generation.LastPresent is { IsCompleted: false })
        {
            drawImage = generation.Image;
            return false;
        }

        drawImage = generation.Image;
        BeginDraw(generation);
        return true;
    }

    private SurfaceGeneration CreateGeneration(PixelSize size) => new()
    {
        Size = size,
        Image = new ImageResource(context, (uint)Format.R8G8B8A8Unorm, size, true, interop.SupportedImageHandleTypes),
        SemaphorePair = new SemaphorePair(context, true)
    };

    private SurfaceGeneration GetOrCreateCurrentGeneration(PixelSize size)
    {
        if (currentGeneration is null)
            return currentGeneration = CreateGeneration(size);

        if (currentGeneration.Size == size)
            return currentGeneration;

        RetireGeneration(currentGeneration);
        return currentGeneration = CreateGeneration(size);
    }

    private void BeginDraw(SurfaceGeneration generation)
    {
        var commandBuffer = context.CreateCommandBuffer();
        context.BeginCommandBuffer(commandBuffer);
        generation.Image.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit);

        if (generation.InitialSubmit)
        {
            generation.InitialSubmit = false;
            context.SubmitCommandBuffer(commandBuffer);
            TrackSubmission(generation, commandBuffer);
            return;
        }

        context.SubmitCommandBuffer(
            commandBuffer,
            [generation.SemaphorePair.ImageAvailableSemaphore],
            [PipelineStageFlags.TransferBit]);
        TrackSubmission(generation, commandBuffer);
    }

    internal void Present()
    {
        if (currentGeneration is not { } generation)
            return;

        var commandBuffer = context.CreateCommandBuffer();
        context.BeginCommandBuffer(commandBuffer);
        generation.Image.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit);
        context.SubmitCommandBuffer(commandBuffer, signalSemaphores: [generation.SemaphorePair.RenderFinishedSemaphore]);
        TrackSubmission(generation, commandBuffer);

        EnsureImportedResources(generation);
        generation.LastPresent = target.UpdateWithSemaphoresAsync(
            generation.ImportedImage!,
            generation.RenderCompletedSemaphore!,
            generation.AvailableSemaphore!);
    }

    public async ValueTask DisposeAsync()
    {
        if (currentGeneration is not null)
        {
            RetireGeneration(currentGeneration);
            currentGeneration = null;
        }

        await pendingDisposeTask;
    }

    private static void TrackSubmission(SurfaceGeneration generation, Context.CommandBuffer commandBuffer)
    {
        if (generation.LastSubmission is not null)
            generation.LastSubmission.ReleaseExternalReference();

        commandBuffer.AddExternalReference();
        generation.LastSubmission = commandBuffer;
    }

    private void EnsureImportedResources(SurfaceGeneration generation)
    {
        generation.AvailableSemaphore ??= interop.ImportSemaphore(generation.SemaphorePair.Export(false));
        generation.RenderCompletedSemaphore ??= interop.ImportSemaphore(generation.SemaphorePair.Export(true));
        generation.ImportedImage ??= interop.ImportImage(
            generation.Image.Export(),
            new PlatformGraphicsExternalImageProperties
            {
                Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm,
                Width = generation.Size.Width,
                Height = generation.Size.Height,
                MemorySize = generation.Image.MemorySize
            });
    }

    private static async Task DisposeImportedResourcesAsync(SurfaceGeneration generation)
    {
        if (generation.ImportedImage is not null)
            await generation.ImportedImage.DisposeAsync();
        if (generation.AvailableSemaphore is not null)
            await generation.AvailableSemaphore.DisposeAsync();
        if (generation.RenderCompletedSemaphore is not null)
            await generation.RenderCompletedSemaphore.DisposeAsync();
    }

    private static void ReleaseTrackedSubmission(Context.CommandBuffer commandBuffer)
    {
        try
        {
            commandBuffer.WaitForCompletion();
        }
        finally
        {
            commandBuffer.ReleaseExternalReference();
        }
    }

    private void RetireGeneration(SurfaceGeneration generation)
    {
        var previousDisposeTask = pendingDisposeTask;
        pendingDisposeTask = DisposeSurfaceResourcesChainAsync(
            previousDisposeTask,
            generation);
    }

    private static async Task DisposeSurfaceResourcesChainAsync(
        Task previousDisposeTask,
        SurfaceGeneration generation)
    {
        try
        {
            await previousDisposeTask;

            if (generation.LastPresent is not null)
                await generation.LastPresent;

            if (generation.LastSubmission is not null)
                ReleaseTrackedSubmission(generation.LastSubmission);

            await DisposeImportedResourcesAsync(generation);
            generation.DisposeOwnedResources();
        }
        catch (VulkanException ex) when (ex.Result == Result.ErrorDeviceLost)
        {
            Log.Warning(ex, "Device lost while disposing retired Vulkan surface resources.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed disposing Vulkan surface resources.");
        }
    }
}
