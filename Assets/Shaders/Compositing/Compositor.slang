
#include "../Shared.slang"
#include "../Tonemapping/agx.slang"
#include "../Tonemapping/aces.slang"
#include "../Tonemapping/filmic.slang"
#include "../Tonemapping/lottes.slang"
#include "../Tonemapping/neutral.slang"
#include "../Tonemapping/reinhard.slang"
#include "../Tonemapping/reinhard2.slang"
#include "../Tonemapping/uchimura.slang"
#include "../Tonemapping/uncharted2.slang"
#include "../Tonemapping/unreal.slang"

[[vk::binding(0, 0)]] [format("rgba32f")] RWTexture2D<float4> inputColorImage;
[[vk::binding(1, 0)]] [format("rgba8")] RWTexture2D<float4> inputAlbedoImage;
[[vk::binding(2, 0)]] [format("rgba16f")] RWTexture2D<float4> inputNormalImage;
[[vk::binding(3, 0)]] [format("r32ui")] RWTexture2D<uint> inputCryptoImage;
[[vk::binding(4, 0)]] [format("rgba16f")] RWTexture2D<float4> inputPositionImage;
[[vk::binding(5, 0)]] [format("rgba32f")] RWTexture2D<float4> inputAdaptiveImage;
[[vk::binding(6, 0)]] [format("rgba8")] RWTexture2D<float4> outputImage;

struct CompositePushConstants
{
    uint selectedInstanceId;
    int visualizationMode;
    float adaptiveTargetError;
    int adaptiveMinSamples;
    int isMoving;
};

[[vk::push_constant]] ConstantBuffer<CompositePushConstants> pc;

enum BufferVisualizationMode : int
{
    BUFFER_VISUALIZATION_FINAL_COLOR = 0,
    BUFFER_VISUALIZATION_ALBEDO = 1,
    BUFFER_VISUALIZATION_NORMALS = 2,
    BUFFER_VISUALIZATION_POSITION = 3,
    BUFFER_VISUALIZATION_CRYPTO_ID = 4,
    BUFFER_VISUALIZATION_ADAPTIVE_THRESHOLD = 5,
};

static const float ADAPTIVE_EPSILON = 1e-6;

float3 applyTonemap(float3 hdrColor) {
    return aces(hdrColor);
}

int2 mapOutputPixelToInputPixel(int2 outputPixel, int2 outputSize, int2 inputSize)
{
    int x = outputPixel.x * inputSize.x / max(outputSize.x, 1);
    int y = outputPixel.y * inputSize.y / max(outputSize.y, 1);
    return clamp(int2(x, y), int2(0, 0), max(inputSize - 1, int2(0, 0)));
}

float2 mapOutputPixelToInputUv(int2 outputPixel, int2 outputSize, int2 inputSize)
{
    float2 outputExtent = float2(max(outputSize.x, 1), max(outputSize.y, 1));
    float2 inputExtent = float2(max(inputSize.x, 1), max(inputSize.y, 1));
    return ((float2(outputPixel) + 0.5) * inputExtent / outputExtent) - 0.5;
}

void computeBilinearFootprint(
    int2 outputPixel,
    int2 outputSize,
    int2 inputSize,
    out int2 p00,
    out int2 p10,
    out int2 p01,
    out int2 p11,
    out float2 frac)
{
    float2 uv = mapOutputPixelToInputUv(outputPixel, outputSize, inputSize);
    int2 basePixel = int2(floor(uv));
    frac = saturate(uv - float2(basePixel));

    int2 minPixel = int2(0, 0);
    int2 maxPixel = max(inputSize - 1, int2(0, 0));
    p00 = clamp(basePixel, minPixel, maxPixel);
    p10 = clamp(basePixel + int2(1, 0), minPixel, maxPixel);
    p01 = clamp(basePixel + int2(0, 1), minPixel, maxPixel);
    p11 = clamp(basePixel + int2(1, 1), minPixel, maxPixel);
}

float4 sampleBilinearRgba32f(RWTexture2D<float4> image, int2 outputPixel, int2 outputSize, int2 inputSize)
{
    int2 p00, p10, p01, p11;
    float2 frac;
    computeBilinearFootprint(outputPixel, outputSize, inputSize, p00, p10, p01, p11, frac);

    float4 top = lerp(image[p00], image[p10], frac.x);
    float4 bottom = lerp(image[p01], image[p11], frac.x);
    return lerp(top, bottom, frac.y);
}

float4 sampleBilinearRgba8(RWTexture2D<float4> image, int2 outputPixel, int2 outputSize, int2 inputSize)
{
    int2 p00, p10, p01, p11;
    float2 frac;
    computeBilinearFootprint(outputPixel, outputSize, inputSize, p00, p10, p01, p11, frac);

    float4 top = lerp(image[p00], image[p10], frac.x);
    float4 bottom = lerp(image[p01], image[p11], frac.x);
    return lerp(top, bottom, frac.y);
}

float3 sampleBilinearNormal(int2 outputPixel, int2 outputSize, int2 inputSize)
{
    int2 p00, p10, p01, p11;
    float2 frac;
    computeBilinearFootprint(outputPixel, outputSize, inputSize, p00, p10, p01, p11, frac);

    float3 top = lerp(inputNormalImage[p00].rgb, inputNormalImage[p10].rgb, frac.x);
    float3 bottom = lerp(inputNormalImage[p01].rgb, inputNormalImage[p11].rgb, frac.x);
    float3 normal = lerp(top, bottom, frac.y);
    float lengthSq = dot(normal, normal);
    return lengthSq > 1e-6 ? normalize(normal) : float3(0.0, 0.0, 1.0);
}

bool isSelectedHit(int2 outputPixel, int2 outputSize, int2 inputSize) {
    if (outputPixel.x < 0 || outputPixel.y < 0 || outputPixel.x >= outputSize.x || outputPixel.y >= outputSize.y)
        return false;

    int2 inputPixel = mapOutputPixelToInputPixel(outputPixel, outputSize, inputSize);
    uint cryptoId = inputCryptoImage[inputPixel];
    if (cryptoId != pc.selectedInstanceId)
        return false;

    float4 packedPosition = inputPositionImage[inputPixel];
    return packedPosition.w > 0.5;
}

float3 adaptiveHeatmap(float progress)
{
    float3 red = float3(0.92, 0.16, 0.10);
    float3 orange = float3(0.98, 0.60, 0.16);
    float3 green = float3(0.15, 0.78, 0.27);
    if (progress < 0.5)
        return lerp(red, orange, progress * 2.0);
    return lerp(orange, green, (progress - 0.5) * 2.0);
}

float3 visualizeAdaptiveThreshold(int2 pixel)
{
    uint inputWidth;
    uint inputHeight;
    inputAdaptiveImage.GetDimensions(inputWidth, inputHeight);
    int2 inputSize = int2(inputWidth, inputHeight);
    uint outputWidth;
    uint outputHeight;
    outputImage.GetDimensions(outputWidth, outputHeight);
    int2 outputSize = int2(outputWidth, outputHeight);
    int2 inputPixel = mapOutputPixelToInputPixel(pixel, outputSize, inputSize);
    float4 adaptiveState = inputAdaptiveImage[inputPixel];
    float meanMetric = adaptiveState.x;
    float metricM2 = adaptiveState.y;
    float totalSamples = adaptiveState.z;
    float converged = adaptiveState.w;

    if (totalSamples < 0.5)
        return float3(0.0);

    float variance = totalSamples > 1.0 ? metricM2 / max(totalSamples - 1.0, 1.0) : 0.0;
    float standardError = sqrt(max(variance, 0.0) / max(totalSamples, 1.0));
    float relativeError = standardError / max(abs(meanMetric), ADAPTIVE_EPSILON);
    float thresholdProgress = pc.adaptiveTargetError <= 0.0
        ? converged
        : clamp(1.0 - (relativeError / max(pc.adaptiveTargetError, 0.0001)), 0.0, 1.0);

    if (totalSamples < float(pc.adaptiveMinSamples))
        thresholdProgress *= clamp(totalSamples / max(float(pc.adaptiveMinSamples), 1.0), 0.0, 1.0);

    return adaptiveHeatmap(max(thresholdProgress, converged));
}

float3 visualizeNormals(int2 pixel)
{
    uint inputWidth;
    uint inputHeight;
    inputNormalImage.GetDimensions(inputWidth, inputHeight);
    int2 inputSize = int2(inputWidth, inputHeight);
    uint outputWidth;
    uint outputHeight;
    outputImage.GetDimensions(outputWidth, outputHeight);
    int2 outputSize = int2(outputWidth, outputHeight);
    return clamp(sampleBilinearNormal(pixel, outputSize, inputSize), 0.0, 1.0);
}

float3 visualizeAlbedo(int2 pixel)
{
    uint inputWidth;
    uint inputHeight;
    inputAlbedoImage.GetDimensions(inputWidth, inputHeight);
    int2 inputSize = int2(inputWidth, inputHeight);
    uint outputWidth;
    uint outputHeight;
    outputImage.GetDimensions(outputWidth, outputHeight);
    int2 outputSize = int2(outputWidth, outputHeight);
    return clamp(sampleBilinearRgba8(inputAlbedoImage, pixel, outputSize, inputSize).rgb, 0.0, 1.0);
}

float3 visualizePosition(int2 pixel, int2 outputSize)
{
    uint inputWidth;
    uint inputHeight;
    inputPositionImage.GetDimensions(inputWidth, inputHeight);
    int2 inputSize = int2(inputWidth, inputHeight);
    int2 inputPixel = mapOutputPixelToInputPixel(pixel, outputSize, inputSize);
    float4 packedPosition = inputPositionImage[inputPixel];
    if (packedPosition.w <= 0.5)
        return float3(0.0);

    float3 position = packedPosition.xyz;
    float3 compressed = position / (1.0 + abs(position));
    return compressed * 0.5 + 0.5;
}

float3 hashIdToColor(uint id)
{
    uint x = id * 1664525u + 1013904223u;
    x ^= x >> 16;
    float3 color = float3(
        float((x >>  0) & 255u),
        float((x >>  8) & 255u),
        float((x >> 16) & 255u)) / 255.0;
    return lerp(color, float3(1.0), 0.15);
}

float3 visualizeCryptoId(int2 pixel, int2 outputSize)
{
    uint inputWidth;
    uint inputHeight;
    inputCryptoImage.GetDimensions(inputWidth, inputHeight);
    int2 inputSize = int2(inputWidth, inputHeight);
    int2 inputPixel = mapOutputPixelToInputPixel(pixel, outputSize, inputSize);
    uint cryptoId = inputCryptoImage[inputPixel];
    if (cryptoId == InvalidInstance)
        return float3(0.0);
    return hashIdToColor(cryptoId);
}

float3 visualizeBuffer(int2 pixel, int2 outputSize, int2 inputSize, float4 hdrImage)
{
    if (pc.visualizationMode == BufferVisualizationMode.BUFFER_VISUALIZATION_ALBEDO)
        return visualizeAlbedo(pixel);
    if (pc.visualizationMode == BufferVisualizationMode.BUFFER_VISUALIZATION_NORMALS)
        return visualizeNormals(pixel);
    if (pc.visualizationMode == BufferVisualizationMode.BUFFER_VISUALIZATION_POSITION)
        return visualizePosition(pixel, outputSize);
    if (pc.visualizationMode == BufferVisualizationMode.BUFFER_VISUALIZATION_CRYPTO_ID)
        return visualizeCryptoId(pixel, outputSize);
    if (pc.visualizationMode == BufferVisualizationMode.BUFFER_VISUALIZATION_ADAPTIVE_THRESHOLD)
        return visualizeAdaptiveThreshold(pixel);
    return applyTonemap(hdrImage.rgb);
}

bool hasSelectionOutline(int2 pixel, int2 outputSize, int2 inputSize, bool centerSelected)
{
    const int outlineRadius = 2;
    for (int y = -outlineRadius; y <= outlineRadius; ++y) {
        for (int x = -outlineRadius; x <= outlineRadius; ++x) {
            if (x == 0 && y == 0)
                continue;

            if (isSelectedHit(pixel + int2(x, y), outputSize, inputSize) != centerSelected)
                return true;
        }
    }

    return false;
}

[shader("compute")]
[numthreads(GROUP_SIZE, GROUP_SIZE, 1)]
void main(uint3 dispatchThreadID : SV_DispatchThreadID) {
    int2 pixel = int2(dispatchThreadID.xy);
    uint outputWidth;
    uint outputHeight;
    outputImage.GetDimensions(outputWidth, outputHeight);
    int2 outputSize = int2(outputWidth, outputHeight);
    uint inputWidth;
    uint inputHeight;
    inputColorImage.GetDimensions(inputWidth, inputHeight);
    int2 inputSize = int2(inputWidth, inputHeight);
    if (pixel.x >= outputSize.x || pixel.y >= outputSize.y)
        return;

    float4 hdrImage = sampleBilinearRgba32f(inputColorImage, pixel, outputSize, inputSize);
    float3 color = visualizeBuffer(pixel, outputSize, inputSize, hdrImage);
    float alpha = pc.visualizationMode == BufferVisualizationMode.BUFFER_VISUALIZATION_FINAL_COLOR
        ? saturate(hdrImage.a)
        : 1.0;

    if (pc.selectedInstanceId == InvalidInstance) {
        outputImage[pixel] = float4(color, alpha);
        return;
    }

    bool centerSelected = isSelectedHit(pixel, outputSize, inputSize);
    bool edge = hasSelectionOutline(pixel, outputSize, inputSize, centerSelected);

    if (edge) {
        const float3 outlineColor = float3(1.0, 0.78, 0.12);
        const float outlineStrength = 0.9;
        color = lerp(color, outlineColor, outlineStrength);
    }

    outputImage[pixel] = float4(color, alpha);
}
