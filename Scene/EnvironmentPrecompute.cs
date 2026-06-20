using System;
using System.Numerics;

namespace UniversalUmap.Rendering.Scenes;

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
