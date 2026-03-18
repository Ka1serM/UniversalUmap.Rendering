// Render policy functions
#ifndef RENDER_POLICY_SLANG
#define RENDER_POLICY_SLANG

#include "../SharedStructs.slang"
#include "../Bindings.slang"
#include "../Core/Core.slang"

bool isInteractiveFrame()
{
    return pushConstants.isMoving != 0;
}

int effectivePrimarySampleCount()
{
    return isInteractiveFrame() ? 1 : max(sceneSettings.renderSettings.samples, 1);
}

int effectiveAoSampleCount()
{
    if (isInteractiveFrame())
        return 1;
    return max(sceneSettings.renderSettings.samples, 1);
}

int effectiveMaxBounceCount()
{
    int configuredMax = max(
        sceneSettings.renderSettings.diffuseBounces,
        max(sceneSettings.renderSettings.specularBounces, sceneSettings.renderSettings.transmissionBounces));
    return configuredMax;
}

int effectiveRussianRouletteStartBounce()
{
    if (isInteractiveFrame())
        return 64;
    return max(1, sceneSettings.renderSettings.russianRouletteStartBounce);
}

#endif // RENDER_POLICY_SLANG
