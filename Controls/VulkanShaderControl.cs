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
    protected static readonly IBrush HitTestBrush = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));

    private CompositionSurfaceVisual? visual;
    private Compositor? compositor;
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
            compositor = selfVisual.Compositor;
            var drawingSurface = compositor.CreateDrawingSurface();
            visual = compositor.CreateSurfaceVisual();
            visual.Size = new(Bounds.Width, Bounds.Height);
            visual.Surface = drawingSurface;
            ElementComposition.SetElementChildVisual(this, visual);

            var interop = await compositor.TryGetCompositionGpuInterop();
            if (!IsCurrentLifecycle(version))
                return;
            if (interop is null)
            {
                ClearCompositionVisual();
                initialized = false;
                return;
            }

            context = await Context.AcquireAsync(compositor);
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
        compositor = null;
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
            if (!surface.TryBeginDraw(pixelSize, out var image))
            {
                QueueNextFrame();
                return;
            }

            try
            {
                OnRasterDraw(context, image);
            }
            finally
            {
                surface.Present();
            }
            frameDirty = false;
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
        if (!running || !initialized || !frameDirty || updateQueued || compositor is null || !IsVisible)
            return;

        updateQueued = true;
        compositor.RequestCompositionUpdate(update);
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
