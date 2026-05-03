using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Serilog;
using Silk.NET.Vulkan;
using StbImageSharp;
using UniversalUmap.Rendering.Core;
using UniversalUmap.Rendering.Vulkan;

namespace UniversalUmap.Rendering.Scenes;

public sealed unsafe class TextureAsset : IDisposable
{
    public readonly record struct HdrTextureSet(TextureAsset Environment, TextureAsset Cdf, TextureAsset IrradianceMap, TextureAsset RadianceMap);
    private readonly record struct MipUploadData(byte[] PixelBytes, uint Width, uint Height);

    private readonly Context context;
    private readonly VulkanDeviceResources deviceResources;

    public string Name { get; }
    public string SourcePath { get; }
    public int Index { get; internal set; } = -1;

    internal VulkanImage Image { get; }
    internal Sampler Sampler { get; }

    private TextureAsset(
        Context context,
        string name,
        string sourcePath,
        ReadOnlySpan<byte> pixelBytes,
        uint width,
        uint height,
        Format format,
        bool generateMipmaps = false)
        : this(
            context,
            name,
            sourcePath,
            [new MipUploadData(pixelBytes.ToArray(), width, height)],
            format,
            generateMipmaps)
    {
    }

    private TextureAsset(
        Context context,
        string name,
        string sourcePath,
        MipUploadData[] mipUploads,
        Format format,
        bool generateMipmaps = false)
    {
        if (mipUploads.Length == 0)
            throw new ArgumentException("At least one mip level is required.", nameof(mipUploads));

        this.context = context;
        deviceResources = new VulkanDeviceResources(context);
        Name = name;
        SourcePath = sourcePath;
        var baseMip = mipUploads[0];
        var mipLevels = generateMipmaps ? CalculateMipLevels(baseMip.Width, baseMip.Height) : (uint)mipUploads.Length;

        var stagingBytesLength = 0;
        foreach (var mip in mipUploads)
            stagingBytesLength = checked(stagingBytesLength + mip.PixelBytes.Length);

        var stagingBytes = new byte[stagingBytesLength];
        var stagingOffset = 0;
        var copies = new BufferImageCopy[mipUploads.Length];
        for (var i = 0; i < mipUploads.Length; i++)
        {
            var mip = mipUploads[i];
            mip.PixelBytes.CopyTo(stagingBytes.AsSpan(stagingOffset));
            copies[i] = new BufferImageCopy
            {
                BufferOffset = (ulong)stagingOffset,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)i, 0, 1),
                ImageOffset = new Offset3D(0, 0, 0),
                ImageExtent = new Extent3D(mip.Width, mip.Height, 1)
            };
            stagingOffset += mip.PixelBytes.Length;
        }

        var staging = new VulkanBuffer(
            context,
            (ulong)stagingBytes.Length,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        staging.Upload(stagingBytes);

        Image = new VulkanImage(
            context,
            (uint)format,
            new PixelSize((int)baseMip.Width, (int)baseMip.Height),
            exportable: false,
            supportedHandleTypes: Array.Empty<string>(),
            mipLevels: mipLevels);

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            MaxAnisotropy = 1f,
            MinLod = 0f,
            MaxLod = mipLevels - 1
        };
        context.Api.CreateSampler(context.Device, in samplerInfo, default, out var sampler).ThrowOnError();
        Sampler = deviceResources.Track(sampler);

        var commandBuffer = context.CreateCommandBuffer();
        context.BeginCommandBuffer(commandBuffer);
        Image.TransitionLayout(
            commandBuffer.InternalHandle,
            ImageLayout.TransferDstOptimal,
            AccessFlags.TransferWriteBit);

        fixed (BufferImageCopy* pCopies = copies)
        {
            context.Api.CmdCopyBufferToImage(
                commandBuffer.InternalHandle,
                staging.Handle,
                Image.InternalHandle,
                ImageLayout.TransferDstOptimal,
                (uint)copies.Length,
                pCopies);
        }

        if (generateMipmaps && mipLevels > 1)
        {
            GenerateMipmaps(commandBuffer.InternalHandle, baseMip.Width, baseMip.Height, mipLevels);
        }
        else
        {
            Image.TransitionLayout(
                commandBuffer.InternalHandle,
                ImageLayout.ShaderReadOnlyOptimal,
                AccessFlags.ShaderReadBit);
        }
        context.RetainForExecution(commandBuffer, staging);
        context.SubmitCommandBuffer(commandBuffer);
        Log.Debug("Uploaded texture '{TextureName}' ({Width}x{Height}, mips={MipLevels}, format={Format}).", Name, baseMip.Width, baseMip.Height, mipLevels, format);
    }

    internal DescriptorImageInfo GetDescriptorImageInfo()
    {
        return new DescriptorImageInfo(Sampler, Image.InternalView, ImageLayout.ShaderReadOnlyOptimal);
    }

    public static TextureAsset CreateHdr(Context context, string sourcePath)
    {
        return CreateHdrWithCdf(context, sourcePath).Environment;
    }

    public static TextureAsset CreateHdr(Context context, string name, ReadOnlySpan<byte> encodedHdrBytes)
    {
        return CreateHdrWithCdf(context, name, encodedHdrBytes).Environment;
    }

    public static HdrTextureSet CreateHdrWithCdf(Context context, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Texture path is empty", nameof(sourcePath));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Texture file not found: {sourcePath}", sourcePath);

        using var stream = File.OpenRead(sourcePath);
        var result = ImageResultFloat.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        var name = Path.GetFileName(sourcePath);
        Log.Information(
            "Decoded HDR texture '{TextureName}' from file: {Width}x{Height} RGBA32F.",
            name,
            result.Width,
            result.Height);
        return CreateHdrWithCdfFromDecoded(
            context,
            name,
            sourcePath,
            result.Data,
            result.Width,
            result.Height);
    }

    public static HdrTextureSet CreateHdrWithCdf(Context context, string name, ReadOnlySpan<byte> encodedHdrBytes)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Texture name is empty", nameof(name));
        if (encodedHdrBytes.IsEmpty)
            throw new ArgumentException("Texture byte payload is empty", nameof(encodedHdrBytes));

        // HDR files are encoded (Radiance .hdr), so decode first, then upload raw float pixels.
        using var stream = new MemoryStream(encodedHdrBytes.ToArray(), writable: false);
        var result = ImageResultFloat.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        Log.Information("Decoded embedded HDR texture '{TextureName}': {Width}x{Height} RGBA32F.", name, result.Width, result.Height);
        return CreateHdrWithCdfFromDecoded(
            context,
            name,
            string.Empty,
            result.Data,
            result.Width,
            result.Height);
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

    public static TextureAsset CreateRgba32Float(
        Context context,
        string name,
        string sourcePath,
        ReadOnlySpan<byte> rgba32fBytes,
        uint width,
        uint height,
        bool generateMipmaps = false)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Texture name is empty", nameof(name));
        if (width == 0 || height == 0)
            throw new ArgumentException("Texture dimensions must be > 0.");
        if (rgba32fBytes.Length != width * height * 4 * sizeof(float))
            throw new ArgumentException("RGBA32F byte payload size does not match width*height*4*sizeof(float).");

        return new TextureAsset(
            context,
            name,
            sourcePath,
            rgba32fBytes,
            width,
            height,
            Format.R32G32B32A32Sfloat,
            generateMipmaps);
    }

    public static TextureAsset CreateRgba16Float(
        Context context,
        string name,
        string sourcePath,
        ReadOnlySpan<byte> rgba16fBytes,
        uint width,
        uint height,
        bool generateMipmaps = false)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Texture name is empty", nameof(name));
        if (width == 0 || height == 0)
            throw new ArgumentException("Texture dimensions must be > 0.");
        if (rgba16fBytes.Length != width * height * 4 * sizeof(ushort))
            throw new ArgumentException("RGBA16F byte payload size does not match width*height*4*sizeof(ushort).");

        return new TextureAsset(
            context,
            name,
            sourcePath,
            rgba16fBytes,
            width,
            height,
            Format.R16G16B16A16Sfloat,
            generateMipmaps);
    }

    public static TextureAsset CreateRgba16FloatMipChain(
        Context context,
        string name,
        string sourcePath,
        byte[][] rgba16fMipBytes,
        uint width,
        uint height)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Texture name is empty", nameof(name));
        if (width == 0 || height == 0)
            throw new ArgumentException("Texture dimensions must be > 0.");
        if (rgba16fMipBytes.Length == 0)
            throw new ArgumentException("At least one mip level is required.", nameof(rgba16fMipBytes));

        var mipUploads = new MipUploadData[rgba16fMipBytes.Length];
        var mipWidth = width;
        var mipHeight = height;
        for (var i = 0; i < rgba16fMipBytes.Length; i++)
        {
            var mipBytes = rgba16fMipBytes[i];
            var expectedByteLength = checked((int)(mipWidth * mipHeight * 4u * sizeof(ushort)));
            if (mipBytes.Length != expectedByteLength)
                throw new ArgumentException($"RGBA16F mip {i} payload size does not match width*height*4*sizeof(ushort).", nameof(rgba16fMipBytes));

            mipUploads[i] = new MipUploadData(mipBytes, mipWidth, mipHeight);
            mipWidth = Math.Max(1u, mipWidth / 2u);
            mipHeight = Math.Max(1u, mipHeight / 2u);
        }

        return new TextureAsset(
            context,
            name,
            sourcePath,
            mipUploads,
            Format.R16G16B16A16Sfloat);
    }

    public static bool TryCreateFromUTexture(
        Context context,
        UTexture texture,
        string sourcePath,
        out TextureAsset? asset)
    {
        asset = null;
        try
        {
            var mip = texture.GetFirstMip();
            if (mip?.BulkData?.Data is not { Length: > 0 } bulkData)
                return false;

            if (!TryMapUnrealToVulkanFormat(texture.Format, out var format))
                return false;

            var width = (uint)Math.Max(1, mip.SizeX);
            var height = (uint)Math.Max(1, mip.SizeY);
            var name = string.IsNullOrWhiteSpace(sourcePath) ? texture.Name : sourcePath;
            asset = new TextureAsset(context, name, sourcePath, bulkData, width, height, format);
            return true;
        }
        catch
        {
            asset = null;
            return false;
        }
    }

    public static TextureAsset LoadHdr(Context context, string sourcePath)
    {
        return CreateHdrWithCdf(context, sourcePath).Environment;
    }

    public static TextureAsset LoadHdr(Context context, string name, ReadOnlySpan<byte> encodedHdrBytes)
    {
        return CreateHdrWithCdf(context, name, encodedHdrBytes).Environment;
    }

    internal static void DisposeSharedStagingRing()
    {
        // No-op: shared staging ring was removed in favor of per-upload retained staging buffers.
    }

    public void Dispose()
    {
        deviceResources.Dispose();
        Image.Dispose();
    }

    private static HdrTextureSet CreateHdrWithCdfFromDecoded(
        Context context,
        string name,
        string sourcePath,
        float[] rgba32f,
        int width,
        int height)
    {
        var hdrBytes = MemoryMarshal.AsBytes(rgba32f.AsSpan()).ToArray();
        var environment = new TextureAsset(
            context,
            name,
            sourcePath,
            hdrBytes,
            (uint)width,
            (uint)height,
            Format.R32G32B32A32Sfloat,
            generateMipmaps: true);

        var derivedMaps = EnvironmentPrecompute.BuildDerivedMaps(rgba32f, width, height);
        var cdfBytes = MemoryMarshal.AsBytes(derivedMaps.Cdf.Rgba32f.AsSpan()).ToArray();
        var cdf = new TextureAsset(
            context,
            $"{name}_cdf",
            sourcePath,
            cdfBytes,
            (uint)derivedMaps.Cdf.Width,
            (uint)derivedMaps.Cdf.Height,
            Format.R32G32B32A32Sfloat);

        var irradianceBytes = MemoryMarshal.AsBytes(derivedMaps.Irradiance.Rgba32f.AsSpan()).ToArray();
        var irradianceMap = new TextureAsset(
            context,
            $"{name}_irradiance",
            sourcePath,
            irradianceBytes,
            (uint)derivedMaps.Irradiance.Width,
            (uint)derivedMaps.Irradiance.Height,
            Format.R32G32B32A32Sfloat);

        var radianceBytes = MemoryMarshal.AsBytes(derivedMaps.Radiance.Rgba32f.AsSpan()).ToArray();
        var radianceMap = new TextureAsset(
            context,
            $"{name}_radiance",
            sourcePath,
            radianceBytes,
            (uint)derivedMaps.Radiance.Width,
            (uint)derivedMaps.Radiance.Height,
            Format.R32G32B32A32Sfloat,
            generateMipmaps: true);

        return new HdrTextureSet(environment, cdf, irradianceMap, radianceMap);
    }

    private static uint CalculateMipLevels(uint width, uint height)
    {
        var maxDimension = Math.Max(width, height);
        return (uint)MathF.Floor(MathF.Log2(maxDimension)) + 1u;
    }

    private void GenerateMipmaps(CommandBuffer commandBuffer, uint width, uint height, uint mipLevels)
    {
        var api = context.Api;
        var mipWidth = (int)width;
        var mipHeight = (int)height;

        for (uint level = 1; level < mipLevels; level++)
        {
            var previousLevel = level - 1;

            TransitionMipLevel(
                api,
                commandBuffer,
                previousLevel,
                ImageLayout.TransferDstOptimal,
                AccessFlags.TransferWriteBit,
                ImageLayout.TransferSrcOptimal,
                AccessFlags.TransferReadBit);

            TransitionMipLevel(
                api,
                commandBuffer,
                level,
                ImageLayout.Undefined,
                AccessFlags.None,
                ImageLayout.TransferDstOptimal,
                AccessFlags.TransferWriteBit);

            var nextWidth = Math.Max(1, mipWidth / 2);
            var nextHeight = Math.Max(1, mipHeight / 2);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, previousLevel, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level, 0, 1)
            };
            blit.SrcOffsets.Element0 = new Offset3D(0, 0, 0);
            blit.SrcOffsets.Element1 = new Offset3D(mipWidth, mipHeight, 1);
            blit.DstOffsets.Element0 = new Offset3D(0, 0, 0);
            blit.DstOffsets.Element1 = new Offset3D(nextWidth, nextHeight, 1);

            api.CmdBlitImage(
                commandBuffer,
                Image.InternalHandle,
                ImageLayout.TransferSrcOptimal,
                Image.InternalHandle,
                ImageLayout.TransferDstOptimal,
                1,
                in blit,
                Filter.Linear);

            TransitionMipLevel(
                api,
                commandBuffer,
                previousLevel,
                ImageLayout.TransferSrcOptimal,
                AccessFlags.TransferReadBit,
                ImageLayout.ShaderReadOnlyOptimal,
                AccessFlags.ShaderReadBit);

            mipWidth = nextWidth;
            mipHeight = nextHeight;
        }

        TransitionMipLevel(
            api,
            commandBuffer,
            mipLevels - 1,
            ImageLayout.TransferDstOptimal,
            AccessFlags.TransferWriteBit,
            ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.ShaderReadBit);

        Image.SetTrackedLayout(ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit);
    }

    private void TransitionMipLevel(
        Vk api,
        CommandBuffer commandBuffer,
        uint mipLevel,
        ImageLayout sourceLayout,
        AccessFlags sourceAccessMask,
        ImageLayout destinationLayout,
        AccessFlags destinationAccessMask)
    {
        VulkanBarriers.Image(
            api,
            commandBuffer,
            Image.InternalHandle,
            ImageAspectFlags.ColorBit,
            sourceLayout,
            sourceAccessMask,
            destinationLayout,
            destinationAccessMask,
            mipLevel);
    }

    private static bool TryMapUnrealToVulkanFormat(EPixelFormat format, out Format vkFormat)
    {
        switch (format)
        {
            case EPixelFormat.PF_B8G8R8A8:
                vkFormat = Format.B8G8R8A8Unorm;
                return true;
            case EPixelFormat.PF_R8G8B8A8:
            case EPixelFormat.PF_A8R8G8B8:
                vkFormat = Format.R8G8B8A8Unorm;
                return true;
            case EPixelFormat.PF_G8:
            case EPixelFormat.PF_R8:
                vkFormat = Format.R8Unorm;
                return true;
            case EPixelFormat.PF_G16:
                vkFormat = Format.R16Unorm;
                return true;
            case EPixelFormat.PF_G16R16:
                vkFormat = Format.R16G16Unorm;
                return true;
            case EPixelFormat.PF_G16R16F:
                vkFormat = Format.R16G16Sfloat;
                return true;
            case EPixelFormat.PF_R16F:
                vkFormat = Format.R16Sfloat;
                return true;
            case EPixelFormat.PF_R32_FLOAT:
                vkFormat = Format.R32Sfloat;
                return true;
            case EPixelFormat.PF_A16B16G16R16:
                vkFormat = Format.R16G16B16A16Unorm;
                return true;
            case EPixelFormat.PF_A32B32G32R32F:
                vkFormat = Format.R32G32B32A32Sfloat;
                return true;
            case EPixelFormat.PF_DXT1:
                vkFormat = Format.BC1RgbaUnormBlock;
                return true;
            case EPixelFormat.PF_DXT3:
                vkFormat = Format.BC2UnormBlock;
                return true;
            case EPixelFormat.PF_DXT5:
                vkFormat = Format.BC3UnormBlock;
                return true;
            case EPixelFormat.PF_BC4:
                vkFormat = Format.BC4UnormBlock;
                return true;
            case EPixelFormat.PF_BC5:
                vkFormat = Format.BC5UnormBlock;
                return true;
            case EPixelFormat.PF_BC6H:
                vkFormat = Format.BC6HSfloatBlock;
                return true;
            case EPixelFormat.PF_BC7:
                vkFormat = Format.BC7UnormBlock;
                return true;
            default:
                vkFormat = default;
                return false;
        }
    }
}
