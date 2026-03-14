#ifndef RAY_GENERATION_GLSL
#define RAY_GENERATION_GLSL

#include "RenderPolicyCommon.glsl"
#include "PrimaryRayCommon.glsl"
#include "AdaptiveSamplingCommon.glsl"
#include "PayloadUtils.glsl"

void primaryRayGen(ivec2 pixelCoord, ivec2 screenSize) {
    if (pixelCoord.x >= screenSize.x || pixelCoord.y >= screenSize.y)
        return;
    const ivec2 pickPixelCoord = ivec2(pixelCoord.x, (screenSize.y - 1) - pixelCoord.y);
    
    #ifdef USE_COMPUTE
        Payload payload;
    #endif

    uvec2 seed = pcg2d(uvec2(pixelCoord) ^ uvec2(pushConstants.frame * 16777619));
    uint rngState = seed.x;
    AdaptiveSamplingContext adaptiveContext = loadAdaptiveSamplingContext(pixelCoord);
    vec3 temporalReference = pushConstants.frame > 0
        ? adaptiveContext.prevColorData.rgb / max(adaptiveContext.exposureScale, EPSILON)
        : vec3(0.0);
    int russianRouletteStartBounce = effectiveRussianRouletteStartBounce();
    int maxBounces = effectiveMaxBounceCount();
    int sampleCount = effectivePrimarySampleCount();
    bool adaptiveEnabled = adaptiveContext.adaptiveSamplingEnabled;
    bool canReuseStablePrimary = isInteractiveFrame() || (sceneSettings.camera.aperture <= 0.0);
    Payload stablePrimaryPayload;

    if (shouldSkipAdaptiveSampling(adaptiveContext)) {
        return;
    }

    vec3 accumulatedColor = vec3(0.0);
    bool hitAnything = false;
    vec3 stableAlbedo = vec3(0.0);
    vec3 stableNormal = vec3(0.0);
    bool stableHit = false;
    AdaptiveSamplingFrameState adaptiveFrameState = createAdaptiveSamplingFrameState();

    for (int sampleIndex = 0; sampleIndex < sampleCount; ++sampleIndex) {
        if (sampleIndex == 0) {
            SamplerState stableSamplerState = initSamplerState(pixelCoord, pushConstants.frame + sampleIndex);
            vec3 stableRayOrigin, stableRayDirection;
            generatePrimaryRay(pixelCoord, screenSize, sceneSettings.camera, stableSamplerState, true, false, stableRayOrigin, stableRayDirection);

            initializePayload(stablePrimaryPayload, rngState, 0u);

            #ifdef USE_COMPUTE
                traceRayCompute(stableRayOrigin, stableRayDirection, 0.001, 1000.0, stablePrimaryPayload);
            #else
                payload = stablePrimaryPayload;
                traceRayEXT(topLevelAS, gl_RayFlagsOpaqueEXT, 0xff, 0, 0, 0, stableRayOrigin, 0.001, stableRayDirection, 1000.0, 0);
                stablePrimaryPayload = payload;
            #endif

            // Selection relies on every mode keeping crypto/position from the stable primary hit.
            imageStore(outputCrypto, pickPixelCoord, uvec4(stablePrimaryPayload.objectIndex, 0, 0, 0));
            if (stablePrimaryPayload.objectIndex != INVALID_INSTANCE)
                imageStore(outputPosition, pickPixelCoord, vec4(stablePrimaryPayload.position, 1.0));
            else
                imageStore(outputPosition, pickPixelCoord, vec4(0));

            stableHit = ((stablePrimaryPayload.flags & ENV_TRANSPARENT) == 0u);
            stableAlbedo = stableHit ? stablePrimaryPayload.albedo : vec3(0.0);
            stableNormal = stableHit ? stablePrimaryPayload.normal : vec3(0.0);
        }

        SamplerState samplerState = initSamplerState(pixelCoord, pushConstants.frame + sampleIndex);
        bool deterministicSample = (sampleIndex == 0);
        bool useThinLens = !isInteractiveFrame();

        vec3 rayOrigin, rayDirection;
        generatePrimaryRay(pixelCoord, screenSize, sceneSettings.camera, samplerState, deterministicSample, useThinLens, rayOrigin, rayDirection);

        vec3 throughput = vec3(1.0);
        vec3 sampleRadiance = vec3(0.0);
        int diffuseCount = 0;
        int specularCount = 0;
        int transmissionCount = 0;

        for (int bounce = 0; bounce < maxBounces; ++bounce) {
            bool reusePrimaryPayload = (sampleIndex == 0) && canReuseStablePrimary && (bounce == 0);
            if (reusePrimaryPayload) {
                payload = stablePrimaryPayload;
            } else {
                initializePayload(payload, rngState, uint(bounce));

                #ifdef USE_COMPUTE
                    traceRayCompute(rayOrigin, rayDirection, 0.001, 1000.0, payload);
                #else
                    traceRayEXT(topLevelAS, gl_RayFlagsOpaqueEXT, 0xff, 0, 0, 0, rayOrigin, 0.001, rayDirection, 1000.0, 0);
                #endif
            }

            rayOrigin = payload.position;
            rngState = payload.rngState;

            if ((payload.flags & BOUNCE_DIFFUSE) != 0u)
                diffuseCount++;
            if ((payload.flags & BOUNCE_SPECULAR) != 0u)
                specularCount++;
            if ((payload.flags & BOUNCE_TRANSMIT) != 0u)
                transmissionCount++;

            if (diffuseCount > sceneSettings.renderSettings.diffuseBounces || specularCount > sceneSettings.renderSettings.specularBounces || transmissionCount > sceneSettings.renderSettings.transmissionBounces)
                payload.flags |= RAY_TERMINATED;

            if (bounce == 0) {
                if ((payload.flags & RAY_TRANSPARENT) != 0u) {
                    if ((payload.flags & RAY_TERMINATED) == 0u)
                        --bounce;
                    continue;
                }

                hitAnything = ((payload.flags & ENV_TRANSPARENT) == 0u);
            }

            sampleRadiance += throughput * payload.emission;
            throughput *= payload.attenuation;
            rayDirection = payload.nextDirection;

            float russianRouletteSurvivalProbability = computeRussianRouletteSurvivalProbability(
                throughput,
                bounce,
                russianRouletteStartBounce);
            if (russianRouletteSurvivalProbability < 1.0) {
                if (rand(payload.rngState) > russianRouletteSurvivalProbability)
                    payload.flags |= RAY_TERMINATED;
                else
                    throughput /= russianRouletteSurvivalProbability;
            }

            if ((payload.flags & RAY_TERMINATED) != 0u)
                break;
        }

        vec3 runningReference = temporalReference;
        if (sampleIndex > 0)
            runningReference = max(runningReference, accumulatedColor / float(sampleIndex));

        int bounceCount = diffuseCount + specularCount + transmissionCount;
        vec3 filteredRadiance = suppressFireflies(
            sampleRadiance,
            runningReference,
            bounceCount,
            diffuseCount,
            specularCount,
            transmissionCount);
        accumulatedColor += filteredRadiance;
        updateAdaptiveSamplingState(adaptiveContext, filteredRadiance, adaptiveFrameState);
        if (adaptiveEnabled && shouldTerminateAdaptiveSampling(adaptiveContext, adaptiveFrameState))
            break;
    }

    float newAlpha = float(hitAnything);
    vec4 finalColorData;
    vec4 finalAdaptiveData;
    finalizeAdaptiveSampling(adaptiveContext, adaptiveFrameState, accumulatedColor, newAlpha, finalColorData, finalAdaptiveData);

    // Stable auxiliary buffers from deterministic sample 0 (no temporal accumulation).
    vec3 finalAlbedo = stableAlbedo;
    vec3 finalNormal = stableNormal;

    imageStore(outputColor, pixelCoord, finalColorData);
    imageStore(outputAlbedo, pixelCoord, vec4(finalAlbedo, 1.0));
    imageStore(outputNormal, pixelCoord, vec4(finalNormal, 0.0));
    imageStore(outputAdaptiveState, pixelCoord, finalAdaptiveData);
}

#endif // RAY_GENERATION_GLSL
