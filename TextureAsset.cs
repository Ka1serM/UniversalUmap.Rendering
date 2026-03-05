using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Serilog;
using Silk.NET.Vulkan;
using StbImageSharp;

namespace UniversalUmap.Rendering;

public sealed unsafe class TextureAsset : IDisposable
{
    private const float LuminanceEpsilon = 1e-8f;

    public readonly record struct HdrTextureSet(TextureAsset Environment, TextureAsset Cdf);

    private readonly Context context;

    public string Name { get; }
    public string SourcePath { get; }
    public int Index { get; internal set; } = -1;

    internal ImageResource Image { get; }
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

        var staging = new GpuBuffer(
            context,
            (ulong)pixelBytes.Length,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        staging.Upload(pixelBytes);

        Image = new ImageResource(
            context,
            (uint)format,
            new PixelSize((int)width, (int)height),
            exportable: false,
            supportedHandleTypes: Array.Empty<string>());

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

        var commandBuffer = context.CreateCommandBuffer();
        commandBuffer.BeginRecording();
        Image.TransitionLayout(
            commandBuffer.InternalHandle,
            ImageLayout.TransferDstOptimal,
            AccessFlags.TransferWriteBit);

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
            Image.InternalHandle,
            ImageLayout.TransferDstOptimal,
            1,
            in copy);

        Image.TransitionLayout(
            commandBuffer.InternalHandle,
            ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.ShaderReadBit);
        commandBuffer.RetainForExecution(staging);
        commandBuffer.SubmitAndWait();
        Log.Information("Uploaded texture '{TextureName}' ({Width}x{Height}, format={Format}).", Name, width, height, format);
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

    public static bool TryCreateFromUTexture(
        Context context,
        UTexture texture,
        string sourcePath,
        int maxDimension,
        out TextureAsset? asset)
    {
        asset = null;
        try
        {
            var mipIndex = texture.GetMipIndexByMaxSize(maxDimension);
            var mip = mipIndex >= 0 ? texture.GetMip(mipIndex) : texture.GetFirstMip();
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
        if (Sampler.Handle != default)
            context.Api.DestroySampler(context.Device, Sampler, default);
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
            Format.R32G32B32A32Sfloat);

        var cdfPixels = BuildEnvironmentCdfTexture(rgba32f, width, height);
        var cdfBytes = MemoryMarshal.AsBytes(cdfPixels.AsSpan()).ToArray();
        var cdf = new TextureAsset(
            context,
            $"{name}_cdf",
            sourcePath,
            cdfBytes,
            (uint)width,
            (uint)height,
            Format.R32G32B32A32Sfloat);
        return new HdrTextureSet(environment, cdf);
    }

    private static float[] BuildEnvironmentCdfTexture(float[] rgba32f, int width, int height)
    {
        var texelCount = width * height;
        var conditionalCdf = new float[texelCount];
        var weights = new float[texelCount];
        var marginalWeights = new float[height];
        var marginalCdf = new float[height];
        var totalWeight = 0f;

        for (var y = 0; y < height; y++)
        {
            var theta = MathF.PI * ((y + 0.5f) / height);
            var sinTheta = MathF.Max(MathF.Sin(theta), LuminanceEpsilon);
            var rowSum = 0f;
            var rowBase = y * width;

            for (var x = 0; x < width; x++)
            {
                var px = (rowBase + x) * 4;
                var rgb = new Vector3(rgba32f[px], rgba32f[px + 1], rgba32f[px + 2]);
                var weighted = MathF.Max(0f, (0.2126f * rgb.X) + (0.7152f * rgb.Y) + (0.0722f * rgb.Z)) * sinTheta;
                weights[rowBase + x] = weighted;
                rowSum += weighted;
            }

            marginalWeights[y] = rowSum;
            totalWeight += rowSum;

            if (rowSum <= LuminanceEpsilon)
            {
                for (var x = 0; x < width; x++)
                    conditionalCdf[rowBase + x] = (x + 1f) / width;
                continue;
            }

            var accum = 0f;
            for (var x = 0; x < width; x++)
            {
                accum += weights[rowBase + x];
                conditionalCdf[rowBase + x] = accum / rowSum;
            }
        }

        if (totalWeight <= LuminanceEpsilon)
        {
            for (var y = 0; y < height; y++)
                marginalCdf[y] = (y + 1f) / height;
            totalWeight = 1f;
        }
        else
        {
            var accum = 0f;
            for (var y = 0; y < height; y++)
            {
                accum += marginalWeights[y];
                marginalCdf[y] = accum / totalWeight;
            }
        }

        var outPixels = new float[texelCount * 4];
        for (var y = 0; y < height; y++)
        {
            var rowBase = y * width;
            var rowWeight = marginalWeights[y];
            var rowPdf = totalWeight > LuminanceEpsilon ? rowWeight / totalWeight : 1f / height;
            for (var x = 0; x < width; x++)
            {
                var texel = rowBase + x;
                var outBase = texel * 4;
                var texelWeight = weights[texel];
                var conditionalPdf = rowWeight > LuminanceEpsilon ? texelWeight / rowWeight : 1f / width;
                var uvPdf = rowPdf * conditionalPdf;
                outPixels[outBase + 0] = conditionalCdf[texel];
                outPixels[outBase + 1] = marginalCdf[y];
                outPixels[outBase + 2] = rowPdf;
                outPixels[outBase + 3] = uvPdf;
            }
        }

        return outPixels;
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
