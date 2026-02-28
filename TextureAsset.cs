using System;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;
using Silk.NET.Vulkan;
using StbImageSharp;

namespace UniversalUmap.Rendering;

public sealed unsafe class TextureAsset : IDisposable
{
    private readonly Context context;

    public string Name { get; }
    public string SourcePath { get; }
    public int Index { get; internal set; } = -1;

    internal Image Image { get; }
    internal DeviceMemory Memory { get; }
    internal ImageView View { get; }
    internal Sampler Sampler { get; }

    private TextureAsset(
        Context context,
        string name,
        string sourcePath,
        ReadOnlySpan<byte> pixelBytes,
        uint width,
        uint height,
        Format format)
    {
        this.context = context;
        Name = name;
        SourcePath = sourcePath;

        using var staging = new GpuBuffer(
            context,
            (ulong)pixelBytes.Length,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            pixelBytes);

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };
        context.Api.CreateImage(context.Device, in imageInfo, default, out var image).ThrowOnError();
        Image = image;

        context.Api.GetImageMemoryRequirements(context.Device, image, out var requirements);
        var memoryTypeIndex = MemoryHelper.FindSuitableMemoryTypeIndex(
            context.Api,
            context.PhysicalDevice,
            requirements.MemoryTypeBits,
            MemoryPropertyFlags.DeviceLocalBit);
        if (memoryTypeIndex < 0)
            throw new InvalidOperationException("Could not find suitable memory type for texture image");

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = (uint)memoryTypeIndex
        };
        context.Api.AllocateMemory(context.Device, in allocInfo, default, out var memory).ThrowOnError();
        Memory = memory;
        context.Api.BindImageMemory(context.Device, Image, Memory, 0).ThrowOnError();

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
        };
        context.Api.CreateImageView(context.Device, in viewInfo, default, out var view).ThrowOnError();
        View = view;

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            MaxAnisotropy = 1f
        };
        context.Api.CreateSampler(context.Device, in samplerInfo, default, out var sampler).ThrowOnError();
        Sampler = sampler;

        var commandBuffer = context.Pool.CreateCommandBuffer();
        commandBuffer.BeginRecording();
        MemoryHelper.TransitionLayout(
            context.Api,
            commandBuffer.InternalHandle,
            Image,
            ImageLayout.Undefined,
            AccessFlags.None,
            ImageLayout.TransferDstOptimal,
            AccessFlags.TransferWriteBit,
            1);

        var copy = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(width, height, 1)
        };
        context.Api.CmdCopyBufferToImage(
            commandBuffer.InternalHandle,
            staging.Handle,
            Image,
            ImageLayout.TransferDstOptimal,
            1,
            in copy);

        MemoryHelper.TransitionLayout(
            context.Api,
            commandBuffer.InternalHandle,
            Image,
            ImageLayout.TransferDstOptimal,
            AccessFlags.TransferWriteBit,
            ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.ShaderReadBit,
            1);
        commandBuffer.Submit();
        context.Api.QueueWaitIdle(context.Queue).ThrowOnError();
        Log.Information("Uploaded texture '{TextureName}' ({Width}x{Height}, format={Format}).", Name, width, height, format);
    }

    internal DescriptorImageInfo GetDescriptorImageInfo()
    {
        return new DescriptorImageInfo(Sampler, View, ImageLayout.ShaderReadOnlyOptimal);
    }

    public static bool TryCreateHdr(Context context, string sourcePath, out TextureAsset? texture)
    {
        try
        {
            texture = CreateHdr(context, sourcePath);
            return true;
        }
        catch
        {
            texture = null;
            return false;
        }
    }

    public static bool TryCreateHdr(Context context, string name, ReadOnlySpan<byte> encodedHdrBytes, out TextureAsset? texture)
    {
        try
        {
            texture = CreateHdr(context, name, encodedHdrBytes);
            return true;
        }
        catch
        {
            texture = null;
            return false;
        }
    }

    public static TextureAsset CreateHdr(Context context, string sourcePath)
    {
        return LoadHdr(context, sourcePath);
    }

    public static TextureAsset CreateHdr(Context context, string name, ReadOnlySpan<byte> encodedHdrBytes)
    {
        return LoadHdr(context, name, encodedHdrBytes);
    }

    public static TextureAsset CreateRgba8(
        Context context,
        string name,
        string sourcePath,
        ReadOnlySpan<byte> rgbaBytes,
        uint width,
        uint height)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Texture name is empty", nameof(name));
        if (width == 0 || height == 0)
            throw new ArgumentException("Texture dimensions must be > 0.");
        if (rgbaBytes.Length != width * height * 4)
            throw new ArgumentException("RGBA byte payload size does not match width*height*4.");

        return new TextureAsset(
            context,
            name,
            sourcePath,
            rgbaBytes,
            width,
            height,
            Format.R8G8B8A8Unorm);
    }

    public static TextureAsset LoadHdr(Context context, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Texture path is empty", nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Texture file not found: {sourcePath}", sourcePath);

        using var stream = File.OpenRead(sourcePath);
        var result = ImageResultFloat.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        var bytes = MemoryMarshal.AsBytes(result.Data.AsSpan()).ToArray();
        Log.Information(
            "Decoded HDR texture '{TextureName}' from file: {Width}x{Height} RGBA32F.",
            Path.GetFileName(sourcePath),
            result.Width,
            result.Height);
        return new TextureAsset(
            context,
            Path.GetFileName(sourcePath),
            sourcePath,
            bytes,
            (uint)result.Width,
            (uint)result.Height,
            Format.R32G32B32A32Sfloat);
    }

    public static TextureAsset LoadHdr(Context context, string name, ReadOnlySpan<byte> encodedHdrBytes)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Texture name is empty", nameof(name));
        if (encodedHdrBytes.IsEmpty)
            throw new ArgumentException("Texture byte payload is empty", nameof(encodedHdrBytes));

        // HDR files are encoded (Radiance .hdr), so decode first, then upload raw float pixels.
        using var stream = new MemoryStream(encodedHdrBytes.ToArray(), writable: false);
        var result = ImageResultFloat.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        var bytes = MemoryMarshal.AsBytes(result.Data.AsSpan()).ToArray();
        Log.Information("Decoded embedded HDR texture '{TextureName}': {Width}x{Height} RGBA32F.", name, result.Width, result.Height);
        return new TextureAsset(
            context,
            name,
            string.Empty,
            bytes,
            (uint)result.Width,
            (uint)result.Height,
            Format.R32G32B32A32Sfloat);
    }

    public void Dispose()
    {
        if (Sampler.Handle != default)
            context.Api.DestroySampler(context.Device, Sampler, default);
        if (View.Handle != default)
            context.Api.DestroyImageView(context.Device, View, default);
        if (Image.Handle != default)
            context.Api.DestroyImage(context.Device, Image, default);
        if (Memory.Handle != default)
            context.Api.FreeMemory(context.Device, Memory, default);
    }
}
