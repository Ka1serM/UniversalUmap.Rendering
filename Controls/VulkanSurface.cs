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
    private const int BufferCount = 3;

    internal sealed class RenderLease
    {
        private readonly SurfaceState state;
        private readonly SurfaceSlot slot;
        internal ImageResource Image { get; }
        internal Silk.NET.Vulkan.Semaphore ImageAvailableSemaphore { get; }
        internal Silk.NET.Vulkan.Semaphore RenderFinishedSemaphore { get; }
        internal bool WaitForAvailability { get; }

        internal RenderLease(
            SurfaceState state,
            SurfaceSlot slot,
            ImageResource image,
            Silk.NET.Vulkan.Semaphore imageAvailableSemaphore,
            Silk.NET.Vulkan.Semaphore renderFinishedSemaphore,
            bool waitForAvailability)
        {
            this.state = state;
            this.slot = slot;
            Image = image;
            ImageAvailableSemaphore = imageAvailableSemaphore;
            RenderFinishedSemaphore = renderFinishedSemaphore;
            WaitForAvailability = waitForAvailability;
        }

        internal SurfaceState State => state;
        internal SurfaceSlot Slot => slot;
    }

    internal sealed class SurfaceSlot
    {
        public required ImageResource Image { get; init; }
        public required SemaphorePair SemaphorePair { get; init; }

        public ICompositionImportedGpuSemaphore? AvailableSemaphore { get; set; }
        public ICompositionImportedGpuSemaphore? RenderCompletedSemaphore { get; set; }
        public ICompositionImportedGpuImage? ImportedImage { get; set; }
        public Task? LastPresent { get; set; }
        public Context.CommandBuffer? LastSubmission { get; set; }
        public bool AwaitAvailabilityOnAcquire { get; set; }
        public bool LeaseAcquired { get; set; }
        public bool ReadyForPresent { get; set; }
        public bool ReadyCallbackQueued { get; set; }
        public long ReadySequence { get; set; }

        public void DisposeOwnedResources()
        {
            SemaphorePair.Dispose();
            Image.Dispose();
        }
    }

    internal sealed class SurfaceState
    {
        public required PixelSize Size { get; init; }
        public required SurfaceSlot[] Slots { get; init; }
        public long NextReadySequence { get; set; }
    }

    private readonly Context context;
    private readonly ICompositionGpuInterop interop;
    private readonly CompositionDrawingSurface target;

    private SurfaceState? currentState;
    private Task pendingDisposeTask = Task.CompletedTask;
    private readonly object sync = new();

    public VulkanSurface(Context context, ICompositionGpuInterop interop, CompositionDrawingSurface target)
    {
        this.context = context;
        this.interop = interop;
        this.target = target;
    }

    public bool TryAcquireRenderLease(PixelSize size, out RenderLease lease)
    {
        lock (sync)
        {
            var state = GetOrCreateCurrentState(size);
            if (state is null)
            {
                lease = null!;
                return false;
            }

            var slot = GetAcquirableSlot(state);
            if (slot is null)
            {
                lease = null!;
                return false;
            }

            slot.LeaseAcquired = true;
            var waitForAvailability = slot.AwaitAvailabilityOnAcquire;
            slot.AwaitAvailabilityOnAcquire = false;
            lease = new RenderLease(
                state,
                slot,
                slot.Image,
                slot.SemaphorePair.ImageAvailableSemaphore,
                slot.SemaphorePair.RenderFinishedSemaphore,
                waitForAvailability);
            return true;
        }
    }

    private SurfaceState CreateState(PixelSize size)
    {
        var slots = new SurfaceSlot[BufferCount];
        for (var i = 0; i < slots.Length; i++)
        {
            slots[i] = new SurfaceSlot
            {
                Image = new ImageResource(context, (uint)Format.R8G8B8A8Unorm, size, true, interop.SupportedImageHandleTypes),
                SemaphorePair = new SemaphorePair(context, true)
            };
        }

        return new SurfaceState
        {
            Size = size,
            Slots = slots
        };
    }

    private SurfaceState? GetOrCreateCurrentState(PixelSize size)
    {
        if (currentState is null)
        {
            if (!pendingDisposeTask.IsCompleted)
                return null;

            return currentState = CreateState(size);
        }

        if (currentState.Size == size)
            return currentState;

        RetireState(currentState);
        currentState = null;
        return null;
    }

    public void CompleteRender(in RenderLease lease, Context.CommandBuffer commandBuffer)
    {
        lock (sync)
        {
            TrackSubmission(lease.Slot, commandBuffer);
            lease.Slot.LeaseAcquired = false;
            lease.Slot.ReadyForPresent = true;
            lease.Slot.ReadySequence = ++lease.State.NextReadySequence;
        }
    }

    public bool TryPresentLatestReadyFrame()
    {
        SurfaceSlot? slot;
        SurfaceState? state;
        lock (sync)
        {
            state = currentState;
            if (state is null)
                return false;

            slot = GetLatestReadySlot(state);
            if (slot is null)
                return false;

            EnsureImportedResources(state, slot);
            slot.LastPresent = target.UpdateWithSemaphoresAsync(
                slot.ImportedImage!,
                slot.RenderCompletedSemaphore!,
                slot.AvailableSemaphore!);
            slot.AwaitAvailabilityOnAcquire = true;
            slot.ReadyForPresent = false;
            slot.ReadyCallbackQueued = false;

            foreach (var otherSlot in state.Slots)
            {
                if (ReferenceEquals(otherSlot, slot) || !otherSlot.ReadyForPresent)
                    continue;

                // Drop older completed frames when a newer one is presented.
                otherSlot.ReadyForPresent = false;
                otherSlot.ReadySequence = 0;
            }

            return true;
        }
    }

    public bool TryScheduleReadyCallback(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        Task? pendingPresent = null;
        lock (sync)
        {
            var state = currentState;
            if (state is null)
                return false;

            var slot = state.Slots.FirstOrDefault(candidate => candidate.LastPresent is { IsCompleted: false } && !candidate.ReadyCallbackQueued);
            if (slot is null)
                return false;

            slot.ReadyCallbackQueued = true;
            pendingPresent = slot.LastPresent;
        }

        pendingPresent!.ContinueWith(
            _ =>
            {
                lock (sync)
                {
                    var state = currentState;
                    if (state is not null)
                    {
                        foreach (var slot in state.Slots)
                        {
                            if (ReferenceEquals(slot.LastPresent, pendingPresent))
                            {
                                slot.ReadyCallbackQueued = false;
                                break;
                            }
                        }
                    }
                }

                callback();
            },
            TaskScheduler.Default);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        SurfaceState? stateToRetire;
        lock (sync)
        {
            stateToRetire = currentState;
            currentState = null;
        }

        if (stateToRetire is not null)
            RetireState(stateToRetire);

        await pendingDisposeTask;
    }

    private static void TrackSubmission(SurfaceSlot slot, Context.CommandBuffer commandBuffer)
    {
        if (slot.LastSubmission is not null)
            slot.LastSubmission.ReleaseExternalReference();

        commandBuffer.AddExternalReference();
        slot.LastSubmission = commandBuffer;
    }

    private void EnsureImportedResources(SurfaceState state, SurfaceSlot slot)
    {
        slot.AvailableSemaphore ??= interop.ImportSemaphore(slot.SemaphorePair.Export(false));
        slot.RenderCompletedSemaphore ??= interop.ImportSemaphore(slot.SemaphorePair.Export(true));
        slot.ImportedImage ??= interop.ImportImage(
            slot.Image.Export(),
            new PlatformGraphicsExternalImageProperties
            {
                Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm,
                Width = state.Size.Width,
                Height = state.Size.Height,
                MemorySize = slot.Image.MemorySize
            });
    }

    private static async Task DisposeImportedResourcesAsync(SurfaceSlot slot)
    {
        if (slot.ImportedImage is not null)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await slot.ImportedImage.DisposeAsync();
            });
        }
        if (slot.AvailableSemaphore is not null)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await slot.AvailableSemaphore.DisposeAsync();
            });
        }
        if (slot.RenderCompletedSemaphore is not null)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await slot.RenderCompletedSemaphore.DisposeAsync();
            });
        }
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

    private void RetireState(SurfaceState state)
    {
        var previousDisposeTask = pendingDisposeTask;
        pendingDisposeTask = DisposeSurfaceResourcesChainAsync(
            previousDisposeTask,
            state);
    }

    private static SurfaceSlot? GetAcquirableSlot(SurfaceState state)
    {
        foreach (var slot in state.Slots)
        {
            if (slot.LeaseAcquired || slot.ReadyForPresent)
                continue;

            if (slot.LastPresent is { IsCompleted: false })
                continue;

            return slot;
        }

        return null;
    }

    private static SurfaceSlot? GetLatestReadySlot(SurfaceState state)
    {
        SurfaceSlot? selected = null;
        foreach (var slot in state.Slots)
        {
            if (!slot.ReadyForPresent)
                continue;

            if (selected is null || slot.ReadySequence > selected.ReadySequence)
                selected = slot;
        }

        return selected;
    }

    private static async Task DisposeSurfaceResourcesChainAsync(
        Task previousDisposeTask,
        SurfaceState state)
    {
        try
        {
            await previousDisposeTask;

            foreach (var slot in state.Slots)
            {
                if (slot.LastPresent is not null)
                    await slot.LastPresent;
            }

            foreach (var slot in state.Slots)
            {
                if (slot.LastSubmission is not null)
                    ReleaseTrackedSubmission(slot.LastSubmission);
            }

            foreach (var slot in state.Slots)
                await DisposeImportedResourcesAsync(slot);

            foreach (var slot in state.Slots)
                slot.DisposeOwnedResources();
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
