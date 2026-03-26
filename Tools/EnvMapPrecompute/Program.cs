using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using StbImageSharp;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: EnvMapPrecompute <input.hdr> <output-prefix>");
    return 1;
}

var hdrPath = Path.GetFullPath(args[0]);
var outputPrefix = Path.GetFullPath(args[1]);
if (!File.Exists(hdrPath))
{
    Console.Error.WriteLine($"Input HDR not found: {hdrPath}");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPrefix)!);

using var stream = File.OpenRead(hdrPath);
var decoded = ImageResultFloat.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
var derived = EnvironmentPrecompute.BuildDerivedMaps(decoded.Data, decoded.Width, decoded.Height);

WritePacked($"{outputPrefix}_env.bin", decoded.Data, decoded.Width, decoded.Height);
WritePacked($"{outputPrefix}_cdf.bin", derived.Cdf.Rgba32f, derived.Cdf.Width, derived.Cdf.Height);
WritePacked($"{outputPrefix}_irradiance.bin", derived.Irradiance.Rgba32f, derived.Irradiance.Width, derived.Irradiance.Height);
WritePacked($"{outputPrefix}_radiance.bin", derived.Radiance.Rgba32f, derived.Radiance.Width, derived.Radiance.Height);

Console.WriteLine($"Generated environment maps for {Path.GetFileName(hdrPath)}");
return 0;

static void WritePacked(string path, float[] rgba32f, int width, int height)
{
    var pixelBytes = MemoryMarshal.AsBytes(rgba32f.AsSpan());
    var packed = new byte[8 + pixelBytes.Length];
    BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(0, 4), width);
    BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(4, 4), height);
    pixelBytes.CopyTo(packed.AsSpan(8));
    File.WriteAllBytes(path, packed);
    Console.WriteLine($"Wrote {Path.GetFileName(path)} ({width}x{height}, {rgba32f.Length / 4} texels).");
}

internal static class EnvironmentPrecompute
{
    private const float LuminanceEpsilon = 1e-8f;
    public const int IrradianceMapSize = 32;
    public const int RadianceMapSize = 256;

    public readonly record struct MapData(float[] Rgba32f, int Width, int Height);
    public readonly record struct DerivedMaps(MapData Cdf, MapData Irradiance, MapData Radiance);

    public static DerivedMaps BuildDerivedMaps(float[] rgba32f, int width, int height)
    {
        if (rgba32f is null)
            throw new ArgumentNullException(nameof(rgba32f));
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Dimensions must be > 0.");
        if (rgba32f.Length != width * height * 4)
            throw new ArgumentException("RGBA32F payload size does not match width*height*4.", nameof(rgba32f));

        return new DerivedMaps(
            new MapData(BuildEnvironmentCdfTexture(rgba32f, width, height), width, height),
            new MapData(CreateIrradianceMap(rgba32f, width, height), IrradianceMapSize, IrradianceMapSize),
            new MapData(CreateRadianceMap(rgba32f, width, height), RadianceMapSize, RadianceMapSize));
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

    private static float[] CreateIrradianceMap(float[] rgba32f, int srcWidth, int srcHeight)
    {
        const int dstSize = IrradianceMapSize;
        const int sampleCount = 128;
        var irradiancePixels = new float[dstSize * dstSize * 4];

        Parallel.For(0, dstSize, y =>
        {
            for (var x = 0; x < dstSize; x++)
            {
                var u = (x + 0.5f) / dstSize;
                var v = (y + 0.5f) / dstSize;
                var phi = (u - 0.5f) * (2.0f * MathF.PI);
                var theta = (1.0f - v) * MathF.PI;

                var sinTheta = MathF.Sin(theta);
                var normal = new Vector3(
                    MathF.Cos(phi) * sinTheta,
                    MathF.Cos(theta),
                    MathF.Sin(phi) * sinTheta);

                var irradiance = Vector3.Zero;
                for (var i = 0; i < sampleCount; i++)
                {
                    var u1 = (i + 0.5f) / sampleCount;
                    var u2 = (i * 0.618033988749895f) % 1f;

                    var r = MathF.Sqrt(u1);
                    var thetaSample = 2.0f * MathF.PI * u2;
                    var xSample = r * MathF.Cos(thetaSample);
                    var ySample = r * MathF.Sin(thetaSample);
                    var zSample = MathF.Sqrt(MathF.Max(0f, 1f - u1));

                    Vector3 tangent;
                    if (MathF.Abs(normal.Y) < 0.99f)
                        tangent = Vector3.Normalize(Vector3.Cross(new Vector3(0, 1, 0), normal));
                    else
                        tangent = Vector3.Normalize(Vector3.Cross(new Vector3(1, 0, 0), normal));

                    var bitangent = Vector3.Cross(normal, tangent);
                    var sampleDir = Vector3.Normalize(tangent * xSample + bitangent * ySample + normal * zSample);

                    var clampedY = MathF.Max(-1f, MathF.Min(1f, sampleDir.Y));
                    var sampleU = MathF.Atan2(sampleDir.Z, sampleDir.X) / (2.0f * MathF.PI) + 0.5f;
                    var sampleV = 1.0f - MathF.Acos(clampedY) / MathF.PI;

                    var srcX = Math.Clamp((int)(sampleU * srcWidth), 0, srcWidth - 1);
                    var srcY = Math.Clamp((int)(sampleV * srcHeight), 0, srcHeight - 1);
                    var srcIdx = (srcY * srcWidth + srcX) * 4;

                    var radiance = new Vector3(rgba32f[srcIdx], rgba32f[srcIdx + 1], rgba32f[srcIdx + 2]);
                    irradiance += radiance;
                }

                irradiance *= MathF.PI / sampleCount;

                var dstIdx = (y * dstSize + x) * 4;
                irradiancePixels[dstIdx + 0] = irradiance.X;
                irradiancePixels[dstIdx + 1] = irradiance.Y;
                irradiancePixels[dstIdx + 2] = irradiance.Z;
                irradiancePixels[dstIdx + 3] = 1f;
            }
        });

        return irradiancePixels;
    }

    private static float[] CreateRadianceMap(float[] rgba32f, int srcWidth, int srcHeight)
    {
        const int dstSize = RadianceMapSize;
        var radiancePixels = new float[dstSize * dstSize * 4];

        var scaleX = srcWidth / (float)dstSize;
        var scaleY = srcHeight / (float)dstSize;

        for (var y = 0; y < dstSize; y++)
        {
            for (var x = 0; x < dstSize; x++)
            {
                var srcX = (int)MathF.Min(x * scaleX, srcWidth - 1);
                var srcY = (int)MathF.Min(y * scaleY, srcHeight - 1);
                var srcIdx = (srcY * srcWidth + srcX) * 4;
                var dstIdx = (y * dstSize + x) * 4;

                radiancePixels[dstIdx + 0] = rgba32f[srcIdx + 0];
                radiancePixels[dstIdx + 1] = rgba32f[srcIdx + 1];
                radiancePixels[dstIdx + 2] = rgba32f[srcIdx + 2];
                radiancePixels[dstIdx + 3] = 1f;
            }
        }

        return radiancePixels;
    }
}
