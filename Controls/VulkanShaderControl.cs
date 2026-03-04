using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using Serilog;
using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering.Controls;

public abstract class VulkanShaderControl : Control
{
    private static readonly IBrush HitTestBrush = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));

    private CompositionSurfaceVisual? visual;
    private Compositor? compositor;
    private bool updateQueued;
    private bool initialized;
    private readonly Action update;
    private Task pendingDisposeTask = Task.CompletedTask;
    private GraphicsResources? resources;

    private sealed class GraphicsResources : IAsyncDisposable
    {
        public SharedRendererContext.Lease RendererLease { get; }
        public Renderer Renderer => RendererLease.Renderer;
        public VulkanSurface Surface { get; }

        public GraphicsResources(SharedRendererContext.Lease rendererLease, VulkanSurface surface)
        {
            RendererLease = rendererLease;
            Surface = surface;
        }

        public async ValueTask DisposeAsync()
        {
            await Surface.DisposeAsync();
            RendererLease.Dispose();
        }
    }

    protected VulkanShaderControl()
    {
        update = UpdateFrame;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Initialize();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (initialized)
            FreeGraphicsResources();

        initialized = false;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(HitTestBrush, new Rect(Bounds.Size));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == BoundsProperty)
            QueueNextFrame();

        base.OnPropertyChanged(change);
    }

    protected abstract void OnRasterDraw(Renderer renderer, ImageResource target);

    private async void Initialize()
    {
        try
        {
            await pendingDisposeTask;

            var selfVisual = ElementComposition.GetElementVisual(this)!;
            compositor = selfVisual.Compositor;
            var drawingSurface = compositor.CreateDrawingSurface();
            visual = compositor.CreateSurfaceVisual();
            visual.Size = new(Bounds.Width, Bounds.Height);
            visual.Surface = drawingSurface;
            ElementComposition.SetElementChildVisual(this, visual);

            var interop = await compositor.TryGetCompositionGpuInterop();
            if (interop is null)
            {
                initialized = false;
                return;
            }

            var rendererLease = await SharedRendererContext.AcquireAsync(compositor);
            if (rendererLease is null)
            {
                initialized = false;
                return;
            }

            var surface = new VulkanSurface(rendererLease.Renderer.Context, interop, drawingSurface);
            resources = new GraphicsResources(rendererLease, surface);
            initialized = true;
            QueueNextFrame();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize VulkanShaderControl.");
            initialized = false;
        }
    }

    private void ReinitializeAfterDeviceLoss()
    {
        initialized = false;
        updateQueued = false;
        SharedRendererContext.Reset();
        FreeGraphicsResources();
        Initialize();
    }

    private void FreeGraphicsResources()
    {
        if (resources is null)
            return;

        var toDispose = resources;
        resources = null;
        var previousDisposeTask = pendingDisposeTask;
        pendingDisposeTask = DisposeSurfaceChainAsync(previousDisposeTask, toDispose);
    }

    private static async Task DisposeSurfaceChainAsync(Task previousDisposeTask, GraphicsResources surfaceResources)
    {
        try
        {
            await previousDisposeTask;
            await surfaceResources.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed disposing Vulkan shader control resources.");
        }
    }

    private void UpdateFrame()
    {
        updateQueued = false;
        var root = this.GetVisualRoot();
        if (root is null || visual is null || resources is null)
            return;

        var pixelSize = PixelSize.FromSize(Bounds.Size, root.RenderScaling);
        visual.Size = new(Bounds.Width, Bounds.Height);
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
        {
            QueueNextFrame();
            return;
        }

        try
        {
            if (!resources.Surface.TryBeginDraw(pixelSize, out var image, out var presentScope) || presentScope is null)
            {
                QueueNextFrame();
                return;
            }

            using (presentScope)
                OnRasterDraw(resources.Renderer, image);
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

        QueueNextFrame();
    }

    private void QueueNextFrame()
    {
        if (!initialized || updateQueued || compositor is null)
            return;

        updateQueued = true;
        compositor.RequestCompositionUpdate(update);
    }
}
