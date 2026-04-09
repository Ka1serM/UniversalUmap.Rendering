using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering.Vulkan;

internal class VulkanSwapchain : SwapchainBase<VulkanSwapchainImage>
{
    private readonly Context _vk;

    public VulkanSwapchain(Context vk, ICompositionGpuInterop interop, CompositionDrawingSurface target) : base(interop, target)
    {
        _vk = vk;
    }

    protected override VulkanSwapchainImage CreateImage(PixelSize size)
    {
        return new VulkanSwapchainImage(_vk, size, Interop, Target);
    }

    public IDisposable BeginDraw(PixelSize size, out VulkanSwapchainImage image)
    {
        var rv = BeginDrawCore(size, out image);
        return rv;
    }
}

internal class VulkanSwapchainImage : ISwapchainImage
{
    private readonly Context _vk;
    private readonly ICompositionGpuInterop _interop;
    private readonly CompositionDrawingSurface _target;
    private readonly VulkanImage _image;
    private readonly SemaphorePair _semaphorePair;
    private ICompositionImportedGpuSemaphore? _availableSemaphore, _renderCompletedSemaphore;
    private ICompositionImportedGpuImage? _importedImage;
    private Task? _lastPresent;
    public VulkanImage Image => _image;
    public SemaphorePair SemaphorePair => _semaphorePair;
    private bool _initial = true;

    public VulkanSwapchainImage(Context vk, PixelSize size, ICompositionGpuInterop interop, CompositionDrawingSurface target)
    {
        _vk = vk;
        _interop = interop;
        _target = target;
        Size = size;
        var format = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Format.B8G8R8A8Unorm : Format.R8G8B8A8Unorm;
        _image = new VulkanImage(vk, (uint)format, size, true, interop.SupportedImageHandleTypes);
        _semaphorePair = new SemaphorePair(vk, true);
    }

    public async ValueTask DisposeAsync()
    {
        if (LastPresent != null)
            await LastPresent;
        if (_importedImage != null)
            await _importedImage.DisposeAsync();

        if (_availableSemaphore != null)
            await _availableSemaphore.DisposeAsync();
        if (_renderCompletedSemaphore != null)
            await _renderCompletedSemaphore.DisposeAsync();
        _semaphorePair.Dispose();
        _image.Dispose();
    }

    public PixelSize Size { get; }

    public Task? LastPresent => _lastPresent;

    public void BeginDraw()
    {
        var buffer = _vk.CreateCommandBuffer();
        _vk.BeginCommandBuffer(buffer);

        _image.SetTrackedLayout(ImageLayout.Undefined, AccessFlags.None);
        _image.TransitionLayout(buffer.InternalHandle,
            ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentReadBit);

        if (_image.IsDirectXBacked)
        {
            _vk.SubmitCommandBuffer(buffer, keyedMutex: new Context.KeyedMutexSubmitInfo
            {
                AcquireKey = 0,
                DeviceMemory = _image.InternalMemory
            });
        }
        else if (_initial)
        {
            _initial = false;
            _vk.SubmitCommandBuffer(buffer);
        }
        else
            _vk.SubmitCommandBuffer(buffer,
                new[] { _semaphorePair.ImageAvailableSemaphore },
                new[] { PipelineStageFlags.AllGraphicsBit });
    }

    public void Present()
    {
        var buffer = _vk.CreateCommandBuffer();
        _vk.BeginCommandBuffer(buffer);
        _image.TransitionLayout(buffer.InternalHandle, ImageLayout.TransferSrcOptimal, AccessFlags.TransferWriteBit);

        if (_image.IsDirectXBacked)
        {
            _vk.SubmitCommandBuffer(buffer, keyedMutex: new Context.KeyedMutexSubmitInfo
            {
                ReleaseKey = 1,
                DeviceMemory = _image.InternalMemory
            });
        }
        else
        {
            _vk.SubmitCommandBuffer(buffer, signalSemaphores: new[] { _semaphorePair.RenderFinishedSemaphore });
            _availableSemaphore ??= _interop.ImportSemaphore(_semaphorePair.Export(false));
            _renderCompletedSemaphore ??= _interop.ImportSemaphore(_semaphorePair.Export(true));
        }

        _importedImage ??= _interop.ImportImage(_image.Export(),
            new PlatformGraphicsExternalImageProperties
            {
                Format = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm : PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm,
                Width = Size.Width,
                Height = Size.Height,
                MemorySize = _image.MemorySize
            });

        if (_image.IsDirectXBacked)
            _lastPresent = _target.UpdateWithKeyedMutexAsync(_importedImage, 1, 0);
        else
            _lastPresent = _target.UpdateWithSemaphoresAsync(_importedImage, _renderCompletedSemaphore!, _availableSemaphore!);
    }
}
