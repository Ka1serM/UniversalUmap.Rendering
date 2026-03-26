// Render policy functions
#ifndef RENDER_POLICY_SLANG
#define RENDER_POLICY_SLANG

#include "../Shared.slang"
#include "../Bindings.slang"
#include "../Core/Core.slang"

bool isInteractiveFrame()
{
    return pushConstants.IsMoving != 0;
}

int effectivePrimarySampleCount()
{
    return max(sceneSettings.RenderSettings.Samples, 1);
}

bool useShaderPixelSize()
{
    return isInteractiveFrame() && pushConstants.PixelSizePercent > 100;
}

int2 effectiveShadingScreenSize(int2 fullScreenSize)
{
    if (!useShaderPixelSize())
        return fullScreenSize;

    int percent = max(pushConstants.PixelSizePercent, 100);
    int width = max(1, int(ceil(float(fullScreenSize.x) * 100.0 / float(percent))));
    int height = max(1, int(ceil(float(fullScreenSize.y) * 100.0 / float(percent))));
    return int2(width, height);
}

int2 mapOutputPixelToShadingPixel(int2 outputPixel, int2 fullScreenSize, int2 shadingScreenSize)
{
    if (all(shadingScreenSize == fullScreenSize))
        return outputPixel;

    int x = int(floor(float(outputPixel.x) * float(shadingScreenSize.x) / float(max(fullScreenSize.x, 1))));
    int y = int(floor(float(outputPixel.y) * float(shadingScreenSize.y) / float(max(fullScreenSize.y, 1))));
    return clamp(int2(x, y), int2(0), shadingScreenSize - 1);
}

int2 shadingPixelStart(int2 shadingPixel, int2 fullScreenSize, int2 shadingScreenSize)
{
    int x = int(ceil(float(shadingPixel.x) * float(fullScreenSize.x) / float(max(shadingScreenSize.x, 1))));
    int y = int(ceil(float(shadingPixel.y) * float(fullScreenSize.y) / float(max(shadingScreenSize.y, 1))));
    return clamp(int2(x, y), int2(0), fullScreenSize - 1);
}

int2 shadingPixelEndExclusive(int2 shadingPixel, int2 fullScreenSize, int2 shadingScreenSize)
{
    int x = int(ceil(float(shadingPixel.x + 1) * float(fullScreenSize.x) / float(max(shadingScreenSize.x, 1))));
    int y = int(ceil(float(shadingPixel.y + 1) * float(fullScreenSize.y) / float(max(shadingScreenSize.y, 1))));
    return clamp(int2(x, y), int2(1), fullScreenSize);
}

bool isShadingLeaderPixel(int2 outputPixel, int2 fullScreenSize, int2 shadingScreenSize)
{
    int2 shadingPixel = mapOutputPixelToShadingPixel(outputPixel, fullScreenSize, shadingScreenSize);
    int2 cellStart = shadingPixelStart(shadingPixel, fullScreenSize, shadingScreenSize);
    return all(outputPixel == cellStart);
}

int effectiveAoSampleCount()
{
    return max(sceneSettings.RenderSettings.Samples, 1);
}

int effectiveMaxBounceCount()
{
    int configuredMax = max(
        sceneSettings.RenderSettings.DiffuseBounces,
        max(sceneSettings.RenderSettings.SpecularBounces, sceneSettings.RenderSettings.TransmissionBounces));
    return configuredMax;
}

int effectiveRussianRouletteStartBounce()
{
    if (isInteractiveFrame())
        return 64;
    return max(1, sceneSettings.RenderSettings.RussianRouletteStartBounce);
}

bool useInteractivePathTracingPreview()
{
    return useShaderPixelSize();
}

#endif // RENDER_POLICY_SLANG
