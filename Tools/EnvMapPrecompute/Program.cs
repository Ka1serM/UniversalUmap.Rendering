using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using StbImageSharp;

const int PackedEnvironmentMagic = 0x504D4555;
const int PackedEnvironmentMipChainMagic = 0x324D4555;
const float MaxHalfFloat = 65504f;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("Usage: EnvMapPrecompute <input.hdr> <output-prefix> [max-texture-resolution]");
    return 1;
}

var hdrPath = Path.GetFullPath(args[0]);
var outputPrefix = Path.GetFullPath(args[1]);
var maxTextureResolution = 512;
if (args.Length == 3 && (!int.TryParse(args[2], out maxTextureResolution) || maxTextureResolution <= 0))
{
    Console.Error.WriteLine($"Invalid max texture resolution: {args[2]}");
    return 1;
}

if (!File.Exists(hdrPath))
{
    Console.Error.WriteLine($"Input HDR not found: {hdrPath}");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPrefix)!);

using var stream = File.OpenRead(hdrPath);
var decoded = ImageResultFloat.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
var downsampled = DownsampleToMaxResolution(decoded.Data, decoded.Width, decoded.Height, maxTextureResolution);
var cdfMap = EnvironmentPrecompute.BuildCdfMap(downsampled.Rgba32f, downsampled.Width, downsampled.Height);
var environmentMipChain = CreateBlurredMipChain(downsampled.Rgba32f, downsampled.Width, downsampled.Height);

WritePackedCompressedMipChain($"{outputPrefix}_env.bin.br", environmentMipChain, PackedTextureFormat.Rgb16Float);
WritePackedCompressed($"{outputPrefix}_cdf.bin.br", cdfMap.Rgba32f, cdfMap.Width, cdfMap.Height, PackedTextureFormat.Rgba16Float);

Console.WriteLine($"Generated compressed environment maps for {Path.GetFileName(hdrPath)} at {downsampled.Width}x{downsampled.Height}.");
return 0;

static (float[] Rgba32f, int Width, int Height) DownsampleToMaxResolution(
    float[] rgba32f,
    int width,
    int height,
    int maxTextureResolution)
{
    var largestDimension = Math.Max(width, height);
    if (largestDimension <= maxTextureResolution)
    {
        Console.WriteLine($"Keeping source environment resolution {width}x{height}.");
        return (rgba32f, width, height);
    }

    var scale = maxTextureResolution / (float)largestDimension;
    var dstWidth = Math.Max(1, (int)MathF.Round(width * scale));
    var dstHeight = Math.Max(1, (int)MathF.Round(height * scale));
    var downsampled = new float[dstWidth * dstHeight * 4];

    var scaleX = width / (float)dstWidth;
    var scaleY = height / (float)dstHeight;

    Parallel.For(0, dstHeight, y =>
    {
        var srcY0 = y * scaleY;
        var srcY1 = (y + 1) * scaleY;
        var firstY = (int)MathF.Floor(srcY0);
        var lastY = (int)MathF.Ceiling(srcY1);

        for (var x = 0; x < dstWidth; x++)
        {
            var srcX0 = x * scaleX;
            var srcX1 = (x + 1) * scaleX;
            var firstX = (int)MathF.Floor(srcX0);
            var lastX = (int)MathF.Ceiling(srcX1);

            var sumR = 0f;
            var sumG = 0f;
            var sumB = 0f;
            var sumA = 0f;
            var totalWeight = 0f;

            for (var sy = firstY; sy < lastY; sy++)
            {
                var clampedY = Math.Clamp(sy, 0, height - 1);
                var weightY = MathF.Min(sy + 1, srcY1) - MathF.Max(sy, srcY0);
                if (weightY <= 0f)
                    continue;

                for (var sx = firstX; sx < lastX; sx++)
                {
                    var wrappedX = ((sx % width) + width) % width;
                    var weightX = MathF.Min(sx + 1, srcX1) - MathF.Max(sx, srcX0);
                    if (weightX <= 0f)
                        continue;

                    var weight = weightX * weightY;
                    var srcIdx = (clampedY * width + wrappedX) * 4;
                    sumR += rgba32f[srcIdx + 0] * weight;
                    sumG += rgba32f[srcIdx + 1] * weight;
                    sumB += rgba32f[srcIdx + 2] * weight;
                    sumA += rgba32f[srcIdx + 3] * weight;
                    totalWeight += weight;
                }
            }

            var invWeight = totalWeight > 0f ? 1f / totalWeight : 1f;
            var dstIdx = (y * dstWidth + x) * 4;
            downsampled[dstIdx + 0] = sumR * invWeight;
            downsampled[dstIdx + 1] = sumG * invWeight;
            downsampled[dstIdx + 2] = sumB * invWeight;
            downsampled[dstIdx + 3] = sumA * invWeight;
        }
    });

    Console.WriteLine($"Downsampled environment from {width}x{height} to {dstWidth}x{dstHeight}.");
    return (downsampled, dstWidth, dstHeight);
}

static EnvironmentPrecompute.MapData[] CreateBlurredMipChain(float[] rgba32f, int width, int height)
{
    var mipCount = CalculateMipLevels(width, height);
    var mipChain = new EnvironmentPrecompute.MapData[mipCount];
    mipChain[0] = new EnvironmentPrecompute.MapData(rgba32f, width, height);

    for (var level = 1; level < mipCount; level++)
    {
        var previous = mipChain[level - 1];
        var mipWidth = Math.Max(1, previous.Width / 2);
        var mipHeight = Math.Max(1, previous.Height / 2);
        mipChain[level] = new EnvironmentPrecompute.MapData(
            DownsampleBlurred(previous.Rgba32f, previous.Width, previous.Height, mipWidth, mipHeight),
            mipWidth,
            mipHeight);
    }

    return mipChain;
}

static int CalculateMipLevels(int width, int height)
{
    var maxDimension = Math.Max(width, height);
    return (int)MathF.Floor(MathF.Log2(maxDimension)) + 1;
}

static float[] DownsampleBlurred(float[] source, int sourceWidth, int sourceHeight, int dstWidth, int dstHeight)
{
    float[] kernel = [1f, 4f, 6f, 4f, 1f];
    var destination = new float[dstWidth * dstHeight * 4];
    var scaleX = sourceWidth / (float)dstWidth;
    var scaleY = sourceHeight / (float)dstHeight;

    Parallel.For(0, dstHeight, y =>
    {
        for (var x = 0; x < dstWidth; x++)
        {
            var sourceCenterX = ((x + 0.5f) * scaleX) - 0.5f;
            var sourceCenterY = ((y + 0.5f) * scaleY) - 0.5f;
            var baseX = (int)MathF.Round(sourceCenterX);
            var baseY = (int)MathF.Round(sourceCenterY);

            var sumR = 0f;
            var sumG = 0f;
            var sumB = 0f;
            var sumA = 0f;
            var weightSum = 0f;

            for (var ky = -2; ky <= 2; ky++)
            {
                var sampleY = Math.Clamp(baseY + ky, 0, sourceHeight - 1);
                var weightY = kernel[ky + 2];
                for (var kx = -2; kx <= 2; kx++)
                {
                    var sampleX = PositiveModulo(baseX + kx, sourceWidth);
                    var weight = weightY * kernel[kx + 2];
                    var sourceIndex = (sampleY * sourceWidth + sampleX) * 4;

                    sumR += source[sourceIndex + 0] * weight;
                    sumG += source[sourceIndex + 1] * weight;
                    sumB += source[sourceIndex + 2] * weight;
                    sumA += source[sourceIndex + 3] * weight;
                    weightSum += weight;
                }
            }

            var invWeight = 1f / weightSum;
            var dstIndex = (y * dstWidth + x) * 4;
            destination[dstIndex + 0] = sumR * invWeight;
            destination[dstIndex + 1] = sumG * invWeight;
            destination[dstIndex + 2] = sumB * invWeight;
            destination[dstIndex + 3] = sumA * invWeight;
        }
    });

    return destination;
}

static int PositiveModulo(int value, int modulus)
{
    var result = value % modulus;
    return result < 0 ? result + modulus : result;
}

static void WritePackedCompressed(string path, float[] rgba32f, int width, int height, PackedTextureFormat format)
{
    WritePackedCompressedMipChain(path, [new EnvironmentPrecompute.MapData(rgba32f, width, height)], format);
}

static void WritePackedCompressedMipChain(string path, EnvironmentPrecompute.MapData[] mipChain, PackedTextureFormat format)
{
    if (mipChain.Length == 0)
        throw new ArgumentException("At least one mip level is required.", nameof(mipChain));

    var channelCount = format == PackedTextureFormat.Rgb16Float ? 3 : 4;
    var hasMipChain = mipChain.Length > 1;
    var headerLength = hasMipChain ? 20 : 16;
    var payloadLength = 0;
    foreach (var mip in mipChain)
        payloadLength = checked(payloadLength + mip.Width * mip.Height * channelCount * sizeof(ushort));

    var width = mipChain[0].Width;
    var height = mipChain[0].Height;
    var packed = new byte[headerLength + payloadLength];
    BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(0, 4), hasMipChain ? PackedEnvironmentMipChainMagic : PackedEnvironmentMagic);
    BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(4, 4), width);
    BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(8, 4), height);
    BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(12, 4), (int)format);
    if (hasMipChain)
        BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(16, 4), mipChain.Length);

    var dstOffset = headerLength;
    for (var level = 0; level < mipChain.Length; level++)
    {
        var mip = mipChain[level];
        var expectedWidth = Math.Max(1, width >> level);
        var expectedHeight = Math.Max(1, height >> level);
        if (mip.Width != expectedWidth || mip.Height != expectedHeight)
            throw new ArgumentException($"Mip {level} has size {mip.Width}x{mip.Height}; expected {expectedWidth}x{expectedHeight}.", nameof(mipChain));
        if (mip.Rgba32f.Length != mip.Width * mip.Height * 4)
            throw new ArgumentException($"Mip {level} RGBA32F payload size does not match width*height*4.", nameof(mipChain));

        var texelCount = mip.Width * mip.Height;
        for (var texel = 0; texel < texelCount; texel++)
        {
            var srcOffset = texel * 4;
            for (var channel = 0; channel < channelCount; channel++)
            {
                var value = Math.Clamp(mip.Rgba32f[srcOffset + channel], -MaxHalfFloat, MaxHalfFloat);
                var bits = BitConverter.HalfToUInt16Bits((Half)value);
                BinaryPrimitives.WriteUInt16LittleEndian(packed.AsSpan(dstOffset, sizeof(ushort)), bits);
                dstOffset += sizeof(ushort);
            }
        }
    }

    using (var file = File.Create(path))
    using (var brotli = new BrotliStream(file, CompressionLevel.SmallestSize))
    {
        brotli.Write(packed);
    }

    var compressedSize = new FileInfo(path).Length;
    Console.WriteLine(
        $"Wrote {Path.GetFileName(path)} ({width}x{height}, mips={mipChain.Length}, {format}, {payloadLength / (channelCount * sizeof(ushort)):N0} texels, {packed.Length:N0} bytes raw, {compressedSize:N0} bytes compressed).");
}

internal enum PackedTextureFormat
{
    Rgb16Float = 1,
    Rgba16Float = 2,
}

internal static class EnvironmentPrecompute
{
    private const float LuminanceEpsilon = 1e-8f;

    public readonly record struct MapData(float[] Rgba32f, int Width, int Height);

    public static MapData BuildCdfMap(float[] rgba32f, int width, int height)
    {
        if (rgba32f is null)
            throw new ArgumentNullException(nameof(rgba32f));
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Dimensions must be > 0.");
        if (rgba32f.Length != width * height * 4)
            throw new ArgumentException("RGBA32F payload size does not match width*height*4.", nameof(rgba32f));

        return new MapData(BuildEnvironmentCdfTexture(rgba32f, width, height), width, height);
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
}
