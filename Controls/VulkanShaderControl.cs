using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
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
    private readonly Action update;
    private Task pendingDisposeTask = Task.CompletedTask;

    private Context? context;
    private VulkanSwapchain? swapchain;
    private OffscreenPresentationBuffer? presentationBuffer;

    protected VulkanShaderControl()
    {
        update = UpdateFrame;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Initialize();
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        if (initialized)
        {
            FreeGraphicsResources();
        }

        initialized = false;
        base.OnDetachedFromLogicalTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        DrawHitTestBackground(context, new Rect(Bounds.Size));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == BoundsProperty)
            InvalidateGpuFrame();

        base.OnPropertyChanged(change);
    }

    protected abstract void OnRasterDraw(Context context, VulkanImage target);
    protected virtual void DrawHitTestBackground(DrawingContext context, Rect bounds)
    {
        context.FillRectangle(HitTestBrush, bounds);
    }

    protected virtual void OnGpuResourcesInvalidated()
    {
    }

    async void Initialize()
    {
        try
        {
            await pendingDisposeTask;

            var selfVisual = ElementComposition.GetElementVisual(this)!;
            avaloniaCompositor = selfVisual.Compositor;
            var drawingSurface = avaloniaCompositor.CreateDrawingSurface();
            visual = avaloniaCompositor.CreateSurfaceVisual();
            visual.Size = new(Bounds.Width, Bounds.Height);
            visual.Surface = drawingSurface;
            ElementComposition.SetElementChildVisual(this, visual);

            var interop = await avaloniaCompositor.TryGetCompositionGpuInterop();
            if (interop is null)
            {
                ClearCompositionVisual();
                initialized = false;
                return;
            }

            context = await Context.AcquireAsync(avaloniaCompositor);
            if (context is null)
            {
                ClearCompositionVisual();
                initialized = false;
                return;
            }

            swapchain = new VulkanSwapchain(context, interop, drawingSurface);
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
        initialized = false;
        updateQueued = false;
        frameDirty = true;
        FreeGraphicsResources();
        ClearCompositionVisual();
        await pendingDisposeTask;
        Initialize();
    }

    private void FreeGraphicsResources()
    {
        OnGpuResourcesInvalidated();
        presentationBuffer?.Dispose();
        presentationBuffer = null;

        if (swapchain is not null)
        {
            var toDispose = swapchain;
            swapchain = null;
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

    private static async Task DisposeSurfaceChainAsync(Task previousDisposeTask, VulkanSwapchain activeSurface)
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

    void UpdateFrame()
    {
        updateQueued = false;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        if (visual is null || swapchain is null || context is null)
            return;
        if (!frameDirty)
            return;

        visual.Size = new(Bounds.Width, Bounds.Height);
        var pixelSize = PixelSize.FromSize(Bounds.Size, topLevel.RenderScaling);
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
            return;

        try
        {
            using (swapchain.BeginDraw(pixelSize, out var swapchainImage))
            {
                Log.Debug("{ControlType} drawing widget frame {Width}x{Height}.", GetType().Name, pixelSize.Width, pixelSize.Height);
                presentationBuffer ??= new OffscreenPresentationBuffer(context);
                presentationBuffer.EnsureSize(pixelSize);
                OnRasterDraw(context, presentationBuffer.ColorImage);
                var commandBuffer = context.CreateCommandBuffer();
                context.BeginCommandBuffer(commandBuffer);
                presentationBuffer.BlitToPresentedImage(commandBuffer, swapchainImage.Image);
                context.SubmitAndWait(commandBuffer, signalSemaphores: [swapchainImage.SemaphorePair.RenderFinishedSemaphore]);
                frameDirty = false;
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

    void QueueNextFrame()
    {
        if (initialized && frameDirty && !updateQueued && avaloniaCompositor != null)
        {
            updateQueued = true;
            avaloniaCompositor.RequestCompositionUpdate(update);
        }
    }
}
