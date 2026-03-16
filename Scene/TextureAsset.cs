using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
    private const float LuminanceEpsilon = 1e-8f;
    private const int IrradianceMapSize = 32;
    private const int RadianceMapSize = 256;

    public readonly record struct HdrTextureSet(TextureAsset Environment, TextureAsset Cdf, TextureAsset IrradianceMap, TextureAsset RadianceMap);

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
        Format format,
        bool generateMipmaps = false)
    {
        this.context = context;
        Name = name;
        SourcePath = sourcePath;
        var mipLevels = generateMipmaps ? CalculateMipLevels(width, height) : 1u;

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
        Sampler = sampler;

        var commandBuffer = context.CreateCommandBuffer();
        context.BeginCommandBuffer(commandBuffer);
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

        if (generateMipmaps && mipLevels > 1)
        {
            GenerateMipmaps(commandBuffer.InternalHandle, width, height, mipLevels);
        }
        else
        {
            Image.TransitionLayout(
                commandBuffer.InternalHandle,
                ImageLayout.ShaderReadOnlyOptimal,
                AccessFlags.ShaderReadBit);
        }
        context.RetainForExecution(commandBuffer, staging);
        context.SubmitAndWait(commandBuffer);
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
            Format.R32G32B32A32Sfloat,
            generateMipmaps: true);

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

        var irradianceMap = CreateIrradianceMap(context, name, sourcePath, rgba32f, width, height);
        var radianceMap = CreateRadianceMap(context, name, sourcePath, rgba32f, width, height);

        return new HdrTextureSet(environment, cdf, irradianceMap, radianceMap);
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

    private static TextureAsset CreateIrradianceMap(Context context, string name, string sourcePath, float[] rgba32f, int srcWidth, int srcHeight)
    {
        // Irradiance map: 32x32, cosine-weighted hemisphere integration for PBR diffuse IBL
        const int dstSize = IrradianceMapSize;
        const int sampleCount = 128;
        var irradiancePixels = new float[dstSize * dstSize * 4];

        Parallel.For(0, dstSize, y =>
        {
            for (int x = 0; x < dstSize; x++)
            {
                // Match shader mapping in directionToEnvironmentUv:
                // u = atan(z, x)/(2*pi)+0.5, v = 1 - acos(y)/pi
                float u = (x + 0.5f) / dstSize;
                float v = (y + 0.5f) / dstSize;
                float phi = (u - 0.5f) * (2.0f * MathF.PI);
                float theta = (1.0f - v) * MathF.PI;

                float sinTheta = MathF.Sin(theta);
                Vector3 normal = new(
                    MathF.Cos(phi) * sinTheta,
                    MathF.Cos(theta),
                    MathF.Sin(phi) * sinTheta);

                Vector3 irradiance = Vector3.Zero;
                for (int i = 0; i < sampleCount; i++)
                {
                    // Cosine-weighted hemisphere sample
                    float u1 = (i + 0.5f) / sampleCount;
                    float u2 = (i * 0.618033988749895f) % 1f;

                    float r = MathF.Sqrt(u1);
                    float thetaSample = 2.0f * MathF.PI * u2;
                    float xSample = r * MathF.Cos(thetaSample);
                    float ySample = r * MathF.Sin(thetaSample);
                    float zSample = MathF.Sqrt(MathF.Max(0f, 1f - u1));

                    // Tangent space to world space
                    Vector3 tangent, bitangent;
                    if (MathF.Abs(normal.Y) < 0.99f)
                    {
                        tangent = Vector3.Normalize(Vector3.Cross(new Vector3(0, 1, 0), normal));
                    }
                    else
                    {
                        tangent = Vector3.Normalize(Vector3.Cross(new Vector3(1, 0, 0), normal));
                    }
                    bitangent = Vector3.Cross(normal, tangent);
                    Vector3 sampleDir = tangent * xSample + bitangent * ySample + normal * zSample;
                    sampleDir = Vector3.Normalize(sampleDir);

                    float clampedY = MathF.Max(-1f, MathF.Min(1f, sampleDir.Y));
                    float sampleU = MathF.Atan2(sampleDir.Z, sampleDir.X) / (2.0f * MathF.PI) + 0.5f;
                    float sampleV = 1.0f - MathF.Acos(clampedY) / MathF.PI;

                    int srcX = Math.Clamp((int)(sampleU * srcWidth), 0, srcWidth - 1);
                    int srcY = Math.Clamp((int)(sampleV * srcHeight), 0, srcHeight - 1);
                    int srcIdx = (srcY * srcWidth + srcX) * 4;

                    Vector3 radiance = new(rgba32f[srcIdx], rgba32f[srcIdx + 1], rgba32f[srcIdx + 2]);
                    // Cosine-weighted hemisphere estimator:
                    // E[L] under p(w)=cos(theta)/pi => irradiance = pi * E[L]
                    irradiance += radiance;
                }

                irradiance *= MathF.PI / sampleCount;

                int dstIdx = (y * dstSize + x) * 4;
                irradiancePixels[dstIdx + 0] = irradiance.X;
                irradiancePixels[dstIdx + 1] = irradiance.Y;
                irradiancePixels[dstIdx + 2] = irradiance.Z;
                irradiancePixels[dstIdx + 3] = 1f;
            }
        });

        var irradianceBytes = MemoryMarshal.AsBytes(irradiancePixels.AsSpan()).ToArray();
        return new TextureAsset(
            context,
            $"{name}_irradiance",
            sourcePath,
            irradianceBytes,
            (uint)dstSize,
            (uint)dstSize,
            Format.R32G32B32A32Sfloat);
    }

    private static TextureAsset CreateRadianceMap(Context context, string name, string sourcePath, float[] rgba32f, int srcWidth, int srcHeight)
    {
        // For specular IBL, we use the mip chain of the original environment map
        // The prefiltered radiance map is already available via mip sampling
        // Here we just create a copy with proper sizing for clarity
        const int dstSize = RadianceMapSize;
        
        // Resize to standard size if needed
        var radiancePixels = new float[dstSize * dstSize * 4];
        
        float scaleX = srcWidth / (float)dstSize;
        float scaleY = srcHeight / (float)dstSize;

        for (int y = 0; y < dstSize; y++)
        {
            for (int x = 0; x < dstSize; x++)
            {
                int srcX = (int)MathF.Min(x * scaleX, srcWidth - 1);
                int srcY = (int)MathF.Min(y * scaleY, srcHeight - 1);
                int srcIdx = (srcY * srcWidth + srcX) * 4;
                int dstIdx = (y * dstSize + x) * 4;
                
                radiancePixels[dstIdx + 0] = rgba32f[srcIdx + 0];
                radiancePixels[dstIdx + 1] = rgba32f[srcIdx + 1];
                radiancePixels[dstIdx + 2] = rgba32f[srcIdx + 2];
                radiancePixels[dstIdx + 3] = 1f;
            }
        }

        var radianceBytes = MemoryMarshal.AsBytes(radiancePixels.AsSpan()).ToArray();
        return new TextureAsset(
            context,
            $"{name}_radiance",
            sourcePath,
            radianceBytes,
            (uint)dstSize,
            (uint)dstSize,
            Format.R32G32B32A32Sfloat,
            generateMipmaps: true);
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
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = sourceAccessMask,
            DstAccessMask = destinationAccessMask,
            OldLayout = sourceLayout,
            NewLayout = destinationLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = Image.InternalHandle,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, mipLevel, 1, 0, 1)
        };

        api.CmdPipelineBarrier(
            commandBuffer,
            PipelineStageFlags.AllCommandsBit,
            PipelineStageFlags.AllCommandsBit,
            0,
            0,
            null,
            0,
            null,
            1,
            in barrier);
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
