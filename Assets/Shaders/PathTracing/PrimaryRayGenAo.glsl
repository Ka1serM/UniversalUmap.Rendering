#ifndef PRIMARY_RAY_GEN_AO_GLSL
#define PRIMARY_RAY_GEN_AO_GLSL

#include "PrimaryRayCommon.glsl"
#include "AdaptiveSamplingCommon.glsl"
#include "PayloadUtils.glsl"

void primaryRayGenAo(ivec2 pixelCoord, ivec2 screenSize)
{
    if (pixelCoord.x >= screenSize.x || pixelCoord.y >= screenSize.y)
        return;

    #ifdef USE_COMPUTE
    AoPayload payload;
    #endif
    const ivec2 pickPixelCoord = ivec2(pixelCoord.x, (screenSize.y - 1) - pixelCoord.y);
    uvec2 seed = pcg2d(uvec2(pixelCoord) ^ uvec2(pushConstants.frame * 16777619));
    uint rngState = seed.x;
    AdaptiveSamplingContext adaptiveContext = loadAdaptiveSamplingContext(pixelCoord);

    if (shouldSkipAdaptiveSampling(adaptiveContext))
        return;

    SamplerState samplerState = initSamplerState(pixelCoord, pushConstants.frame);
    vec3 rayOrigin, rayDirection;
    generatePrimaryRay(pixelCoord, screenSize, sceneSettings.camera, samplerState, true, false, rayOrigin, rayDirection);

    initializeAoPayload(payload, rngState);

    #ifdef USE_COMPUTE
        traceRayCompute(rayOrigin, rayDirection, 0.001, 1000.0, payload);
    #else
        traceRayEXT(topLevelAS, gl_RayFlagsOpaqueEXT, 0xff, 0, 0, 0, rayOrigin, 0.001, rayDirection, 1000.0, 0);
    #endif

    imageStore(outputCrypto, pickPixelCoord, uvec4(payload.objectIndex, 0, 0, 0));
    if (payload.objectIndex != INVALID_INSTANCE)
        imageStore(outputPosition, pickPixelCoord, vec4(payload.position, 1.0));
    else
        imageStore(outputPosition, pickPixelCoord, vec4(0));

    AdaptiveSamplingFrameState adaptiveFrameState = createAdaptiveSamplingFrameState();
    updateAdaptiveSamplingState(adaptiveContext, payload.emission, adaptiveFrameState);

    float newAlpha = float((payload.flags & ENV_TRANSPARENT) == 0u);
    vec4 finalColorData;
    vec4 finalAdaptiveData;
    finalizeAdaptiveSampling(adaptiveContext, adaptiveFrameState, payload.emission, newAlpha, finalColorData, finalAdaptiveData);

    vec3 finalAlbedo = newAlpha > 0.0 ? payload.albedo : vec3(0.0);
    vec3 finalNormal = newAlpha > 0.0 ? payload.normal : vec3(0.0);

    imageStore(outputColor, pixelCoord, finalColorData);
    imageStore(outputAlbedo, pixelCoord, vec4(finalAlbedo, 1.0));
    imageStore(outputNormal, pixelCoord, vec4(finalNormal, 0.0));
    imageStore(outputAdaptiveState, pixelCoord, finalAdaptiveData);
}

#endif
