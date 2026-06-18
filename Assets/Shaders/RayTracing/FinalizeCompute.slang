#ifndef RAYTRACING_FINALIZE_SLANG
#define RAYTRACING_FINALIZE_SLANG

#include "../Shared.slang"
#include "../Bindings.slang"

[[vk::push_constant]] ConstantBuffer<PushDataGpu> pushConstants;

#include "Common.slang"
#include "../Core/AdaptiveSampling.slang"
#include "Integrators.slang"

[shader("compute")]
[numthreads(GROUP_SIZE, GROUP_SIZE, 1)]
void main(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    int2 shadingPixel = int2(dispatchThreadID.xy);
    uint screenWidth;
    uint screenHeight;
    outputColor.GetDimensions(screenWidth, screenHeight);
    int2 screenSize = int2(screenWidth, screenHeight);
    int2 shadingScreenSize = computeShadingScreenSize(screenSize);
    if (shadingPixel.x >= shadingScreenSize.x || shadingPixel.y >= shadingScreenSize.y)
        return;

    int2 blockStart = shadingPixelStart(shadingPixel, screenSize, shadingScreenSize);
    int2 blockEnd = shadingPixelEndExclusive(shadingPixel, screenSize, shadingScreenSize);
    AdaptiveSamplingGateContext blockAdaptiveContext = loadAdaptiveSamplingGateContext(blockStart);
    if (shouldSkipAdaptiveSampling(blockAdaptiveContext))
        return;

    uint sampleCount = integratorSampleCount();
    uint baseSlot = computeSampleSlot(shadingPixel, shadingScreenSize, 0u, sampleCount);

    float exposureScale = loadExposureScale();
    float3 referenceColor = float3(0.0);
    if (pushConstants.PixelSizePercent <= 100 && pushConstants.IsMoving == 0)
        referenceColor = loadTemporalReferenceColor(blockStart, exposureScale);

    float3 accumulatedColor = float3(0.0);
    PathState stableState = pathStates[baseSlot];
    AdaptiveSamplingFrameContext adaptiveFrameContext = loadAdaptiveSamplingFrameContext();
    AdaptiveSamplingFrameState adaptiveFrameState = createAdaptiveSamplingFrameState();

    if (adaptiveFrameContext.adaptiveSamplingEnabled) {
        for (uint sampleIndex = 0u; sampleIndex < sampleCount; ++sampleIndex) {
            PathState sampleState = pathStates[baseSlot + sampleIndex];
            float3 filteredColor = integratorFilterRadiance(sampleState, referenceColor);
            accumulatedColor += filteredColor;
            updateAdaptiveSamplingState(adaptiveFrameContext, filteredColor, adaptiveFrameState);
        }
    } else {
        for (uint sampleIndex = 0u; sampleIndex < sampleCount; ++sampleIndex) {
            PathState sampleState = pathStates[baseSlot + sampleIndex];
            accumulatedColor += integratorFilterRadiance(sampleState, referenceColor);
            adaptiveFrameState.samplesTaken++;
        }
    }

    float newAlpha = float(getHitAnything(stableState));

    for (int y = blockStart.y; y < blockEnd.y; ++y) {
        for (int x = blockStart.x; x < blockEnd.x; ++x) {
            int2 outputPixel = int2(x, y);
            AdaptiveSamplingContext adaptiveContext = loadAdaptiveSamplingContext(outputPixel);
            float4 finalColorData;
            float4 finalAdaptiveData;
            finalizeAdaptiveSampling(
                adaptiveContext,
                adaptiveFrameState,
                accumulatedColor,
                newAlpha,
                finalColorData,
                finalAdaptiveData);

            outputCrypto[outputPixel] = stableState.PrimaryObjectIndex;
            outputPosition[outputPixel] = stableState.PrimaryObjectIndex != InvalidInstance
                ? float4(stableState.PrimaryPosition, 1.0)
                : float4(0.0);

            outputColor[outputPixel] = finalColorData;
            outputAdaptiveState[outputPixel] = finalAdaptiveData;
            outputAlbedo[outputPixel] = float4(stableState.PrimaryAlbedo, 1.0);
            outputNormal[outputPixel] = float4(stableState.PrimaryNormal, 0.0);
        }
    }
}

#endif
