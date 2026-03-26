
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
    int pickBuffersFlippedY;
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

int2 toPickPixel(int2 outputPixel, int2 outputSize) {
    if (pc.pickBuffersFlippedY == 0)
        return outputPixel;
    return int2(outputPixel.x, (outputSize.y - 1) - outputPixel.y);
}

int2 mapOutputPixelToInputPixel(int2 outputPixel, int2 outputSize, int2 inputSize)
{
    int x = outputPixel.x * inputSize.x / max(outputSize.x, 1);
    int y = outputPixel.y * inputSize.y / max(outputSize.y, 1);
    return clamp(int2(x, y), int2(0, 0), max(inputSize - 1, int2(0, 0)));
}

bool isSelectedHit(int2 outputPixel, int2 outputSize, int2 inputSize) {
    if (outputPixel.x < 0 || outputPixel.y < 0 || outputPixel.x >= outputSize.x || outputPixel.y >= outputSize.y)
        return false;

    int2 inputPixel = mapOutputPixelToInputPixel(outputPixel, outputSize, inputSize);
    int2 pickPixel = toPickPixel(inputPixel, inputSize);
    uint cryptoId = inputCryptoImage[pickPixel];
    if (cryptoId != pc.selectedInstanceId)
        return false;

    float4 packedPosition = inputPositionImage[pickPixel];
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
    float4 adaptiveState = inputAdaptiveImage[pixel];
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
    return clamp(inputNormalImage[pixel].rgb, 0.0, 1.0);
}

float3 visualizeAlbedo(int2 pixel)
{
    return clamp(inputAlbedoImage[pixel].rgb, 0.0, 1.0);
}

float3 visualizePosition(int2 pixel, int2 outputSize)
{
    uint inputWidth;
    uint inputHeight;
    inputPositionImage.GetDimensions(inputWidth, inputHeight);
    int2 inputSize = int2(inputWidth, inputHeight);
    int2 inputPixel = mapOutputPixelToInputPixel(pixel, outputSize, inputSize);
    int2 pickPixel = toPickPixel(inputPixel, inputSize);
    float4 packedPosition = inputPositionImage[pickPixel];
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
    int2 pickPixel = toPickPixel(inputPixel, inputSize);
    uint cryptoId = inputCryptoImage[pickPixel];
    if (cryptoId == InvalidInstance)
        return float3(0.0);
    return hashIdToColor(cryptoId);
}

float3 visualizeBuffer(int2 pixel, int2 outputSize, int2 inputSize, float4 hdrImage)
{
    if (pc.visualizationMode == BufferVisualizationMode.BUFFER_VISUALIZATION_ALBEDO)
        return visualizeAlbedo(mapOutputPixelToInputPixel(pixel, outputSize, inputSize));
    if (pc.visualizationMode == BufferVisualizationMode.BUFFER_VISUALIZATION_NORMALS)
        return visualizeNormals(mapOutputPixelToInputPixel(pixel, outputSize, inputSize));
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

    int2 inputPixel = mapOutputPixelToInputPixel(pixel, outputSize, inputSize);
    float4 hdrImage = inputColorImage[inputPixel];
    float3 color = visualizeBuffer(pixel, outputSize, inputSize, hdrImage);

    if (pc.selectedInstanceId == InvalidInstance) {
        outputImage[pixel] = float4(color, hdrImage.a);
        return;
    }

    bool centerSelected = isSelectedHit(pixel, outputSize, inputSize);
    bool edge = hasSelectionOutline(pixel, outputSize, inputSize, centerSelected);

    if (edge) {
        const float3 outlineColor = float3(1.0, 0.78, 0.12);
        const float outlineStrength = 0.9;
        color = lerp(color, outlineColor, outlineStrength);
    }

    outputImage[pixel] = float4(color, hdrImage.a);
}
