#ifndef RENDER_POLICY_COMMON_GLSL
#define RENDER_POLICY_COMMON_GLSL

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
    if (isInteractiveFrame())
        return min(configuredMax, 2);
    return configuredMax;
}

int effectiveRussianRouletteStartBounce()
{
    if (isInteractiveFrame())
        return 64;
    return max(1, sceneSettings.renderSettings.russianRouletteStartBounce);
}

#endif
