using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using Serilog;
using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Core;
using UniversalUmap.Rendering.Vulkan;

namespace UniversalUmap.Rendering.Controls;

public abstract class VulkanShaderControl : ContentControl
{
    protected static readonly IBrush HitTestBrush = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));

    private CompositionSurfaceVisual? visual;
    private Avalonia.Rendering.Composition.Compositor? avaloniaCompositor;
    private bool updateQueued;
    private bool frameDirty = true;
    private bool initialized;
    private bool running;
    private long lifecycleVersion;
    private readonly Action update;
    private Task pendingDisposeTask = Task.CompletedTask;
    private Size lastLayoutSize;
    private double lastLayoutScaling = -1d;

    private Context? context;
    private VulkanSurface? surface;

    protected VulkanShaderControl()
    {
        update = UpdateFrame;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        StartControl();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        PauseControl();
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        DrawHitTestBackground(context, new Rect(Bounds.Size));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == BoundsProperty || change.Property == IsVisibleProperty)
            InvalidateGpuFrame();

        base.OnPropertyChanged(change);
    }

    protected abstract void OnRasterDraw(Context context, ImageResource target);
    protected virtual void DrawHitTestBackground(DrawingContext context, Rect bounds)
    {
        context.FillRectangle(HitTestBrush, bounds);
    }

    protected virtual void OnGpuResourcesInvalidated()
    {
    }

    private void StartControl()
    {
        if (running)
            return;

        running = true;
        lifecycleVersion++;
        LayoutUpdated += OnLayoutUpdated;
        lastLayoutSize = default;
        lastLayoutScaling = -1d;
        if (initialized)
        {
            InvalidateGpuFrame();
            return;
        }

        _ = InitializeAsync(lifecycleVersion);
    }

    private void PauseControl()
    {
        if (!running)
            return;

        running = false;
        lifecycleVersion++;
        LayoutUpdated -= OnLayoutUpdated;
        updateQueued = false;
    }

    private async Task InitializeAsync(long version)
    {
        try
        {
            await pendingDisposeTask;
            if (!IsCurrentLifecycle(version))
                return;

            var selfVisual = ElementComposition.GetElementVisual(this)!;
            avaloniaCompositor = selfVisual.Compositor;
            var drawingSurface = avaloniaCompositor.CreateDrawingSurface();
            visual = avaloniaCompositor.CreateSurfaceVisual();
            visual.Size = new(Bounds.Width, Bounds.Height);
            visual.Surface = drawingSurface;
            ElementComposition.SetElementChildVisual(this, visual);

            var interop = await avaloniaCompositor.TryGetCompositionGpuInterop();
            if (!IsCurrentLifecycle(version))
                return;
            if (interop is null)
            {
                ClearCompositionVisual();
                initialized = false;
                return;
            }

            context = await Context.AcquireAsync(avaloniaCompositor);
            if (!IsCurrentLifecycle(version))
            {
                return;
            }
            if (context is null)
            {
                ClearCompositionVisual();
                initialized = false;
                return;
            }

            surface = new VulkanSurface(context, interop, drawingSurface);
            initialized = true;
            Log.Debug("{ControlType} initialized Vulkan shader control resources.", GetType().Name);
            InvalidateGpuFrame();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize VulkanShaderControl.");
            ClearCompositionVisual();
            initialized = false;
        }
    }

    private async void ReinitializeAfterDeviceLoss()
    {
        var version = lifecycleVersion;
        initialized = false;
        updateQueued = false;
        frameDirty = true;
        FreeGraphicsResources();
        ClearCompositionVisual();
        await pendingDisposeTask;
        if (!IsCurrentLifecycle(version))
            return;
        _ = InitializeAsync(version);
    }

    private void FreeGraphicsResources()
    {
        OnGpuResourcesInvalidated();

        if (surface is not null)
        {
            var toDispose = surface;
            surface = null;
            var previousDisposeTask = pendingDisposeTask;
            pendingDisposeTask = DisposeSurfaceChainAsync(previousDisposeTask, toDispose);
        }

        context = null;
    }

    private void ClearCompositionVisual()
    {
        if (visual is not null)
            ElementComposition.SetElementChildVisual(this, null);

        visual = null;
        avaloniaCompositor = null;
    }

    private static async Task DisposeSurfaceChainAsync(Task previousDisposeTask, VulkanSurface activeSurface)
    {
        try
        {
            await previousDisposeTask;
            await activeSurface.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed disposing Vulkan shader control resources.");
        }
    }

    private void UpdateFrame()
    {
        updateQueued = false;
        if (!running)
            return;

        var root = this.GetVisualRoot();
        if (root is null || visual is null || surface is null || context is null)
            return;
        if (!frameDirty)
            return;

        var pixelSize = PixelSize.FromSize(Bounds.Size, root.RenderScaling);
        visual.Size = new(Bounds.Width, Bounds.Height);
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
            return;

        try
        {
            if (!surface.TryAcquireRenderLease(pixelSize, out var lease))
            {
                QueueNextFrame();
                return;
            }

            try
            {
                if (lease.WaitForAvailability)
                {
                    var waitCommandBuffer = context.CreateCommandBuffer();
                    context.BeginCommandBuffer(waitCommandBuffer);
                    Log.Debug("{ControlType} waiting for surface availability.", GetType().Name);
                    context.SubmitAndWait(
                        waitCommandBuffer,
                        [lease.ImageAvailableSemaphore],
                        [PipelineStageFlags.ColorAttachmentOutputBit]);
                }

                Log.Debug("{ControlType} drawing widget frame {Width}x{Height}.", GetType().Name, pixelSize.Width, pixelSize.Height);
                OnRasterDraw(context, lease.Image);
                var commandBuffer = context.CreateCommandBuffer();
                context.BeginCommandBuffer(commandBuffer);
                lease.Image.TransitionLayout(commandBuffer.InternalHandle, ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit);
                context.SubmitAndWait(commandBuffer, signalSemaphores: [lease.RenderFinishedSemaphore]);
                surface.CompleteRender(lease, commandBuffer);
            }
            finally
            {
                if (surface.TryPresentLatestReadyFrame())
                {
                    frameDirty = false;
                }
                else
                {
                    QueueNextFrame();
                }
            }
        }
        catch (VulkanException ex) when (ex.Result == Result.ErrorDeviceLost)
        {
            Log.Error(ex, "Vulkan device lost in VulkanShaderControl. Reinitializing control resources.");
            ReinitializeAfterDeviceLoss();
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Render frame failed in VulkanShaderControl; skipping frame.");
            QueueNextFrame();
            return;
        }
    }

    protected void InvalidateGpuFrame()
    {
        frameDirty = true;
        QueueNextFrame();
    }

    private void QueueNextFrame()
    {
        if (!running || !initialized || !frameDirty || updateQueued || avaloniaCompositor is null)
            return;

        updateQueued = true;
        avaloniaCompositor.RequestCompositionUpdate(update);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        var root = this.GetVisualRoot();
        var scale = root?.RenderScaling ?? -1d;
        var size = Bounds.Size;
        if (size == lastLayoutSize && Math.Abs(scale - lastLayoutScaling) < 0.0001d)
            return;

        lastLayoutSize = size;
        lastLayoutScaling = scale;
        InvalidateGpuFrame();
    }

    private bool IsCurrentLifecycle(long version) => running && lifecycleVersion == version;
}
