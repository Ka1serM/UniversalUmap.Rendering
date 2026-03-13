#ifndef RAY_GENERATION_GLSL
#define RAY_GENERATION_GLSL

#include "PrimaryRayCommon.glsl"
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
    vec4 prevColorData = imageLoad(outputColor, pixelCoord);
    float exposureScale = exp2(sceneSettings.renderSettings.exposure);
    vec3 temporalReference = pushConstants.frame > 0
        ? prevColorData.rgb / max(exposureScale, EPSILON)
        : vec3(0.0);

    vec3 accumulatedColor = vec3(0.0);
    bool hitAnything = false;
    vec3 stableAlbedo = vec3(0.0);
    vec3 stableNormal = vec3(0.0);
    bool stableHit = false;

    for (int sampleIndex = 0; sampleIndex < sceneSettings.renderSettings.samples; ++sampleIndex) {
        if (sampleIndex == 0) {
            SamplerState stableSamplerState = initSamplerState(pixelCoord, pushConstants.frame + sampleIndex);
            vec3 stableRayOrigin, stableRayDirection;
            generatePrimaryRay(pixelCoord, screenSize, sceneSettings.camera, stableSamplerState, true, false, stableRayOrigin, stableRayDirection);

            initializePayload(payload, rngState, 0u);

            #ifdef USE_COMPUTE
                traceRayCompute(stableRayOrigin, stableRayDirection, 0.001, 1000.0, payload);
            #else
                traceRayEXT(topLevelAS, gl_RayFlagsOpaqueEXT, 0xff, 0, 0, 0, stableRayOrigin, 0.001, stableRayDirection, 1000.0, 0);
            #endif

            // Selection relies on every mode keeping crypto/position from the stable primary hit.
            imageStore(outputCrypto, pickPixelCoord, uvec4(payload.objectIndex, 0, 0, 0));
            if (payload.objectIndex != INVALID_INSTANCE)
                imageStore(outputPosition, pickPixelCoord, vec4(payload.position, 1.0));
            else
                imageStore(outputPosition, pickPixelCoord, vec4(0));

            stableHit = ((payload.flags & ENV_TRANSPARENT) == 0u);
            stableAlbedo = stableHit ? payload.albedo : vec3(0.0);
            stableNormal = stableHit ? payload.normal : vec3(0.0);
        }

        SamplerState samplerState = initSamplerState(pixelCoord, pushConstants.frame + sampleIndex);
        bool deterministicSample = (sampleIndex == 0);

        vec3 rayOrigin, rayDirection;
        generatePrimaryRay(pixelCoord, screenSize, sceneSettings.camera, samplerState, deterministicSample, true, rayOrigin, rayDirection);

        vec3 throughput = vec3(1.0);
        vec3 sampleRadiance = vec3(0.0);
        int diffuseCount = 0;
        int specularCount = 0;
        int transmissionCount = 0;
        int maxBounces = max(sceneSettings.renderSettings.diffuseBounces, max(sceneSettings.renderSettings.specularBounces, sceneSettings.renderSettings.transmissionBounces));

        for (int bounce = 0; bounce < maxBounces; ++bounce) {
            initializePayload(payload, rngState, uint(bounce));

            #ifdef USE_COMPUTE
                traceRayCompute(rayOrigin, rayDirection, 0.001, 1000.0, payload);
            #else
                traceRayEXT(topLevelAS, gl_RayFlagsOpaqueEXT, 0xff, 0, 0, 0, rayOrigin, 0.001, rayDirection, 1000.0, 0);
            #endif

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

            //RUSSIAN ROULETTE TERMINATION
            if (bounce > 2) { //start RR after a few bounces
                float p_continue = clamp(luminance(throughput), 0.05, 1.0);
                if (rand(payload.rngState) > p_continue) 
                  payload.flags |= RAY_TERMINATED;
                else
                  throughput /= p_continue; // keep estimator unbiased
            }

            if ((payload.flags & RAY_TERMINATED) != 0u)
                break;
        }

        vec3 runningReference = temporalReference;
        if (sampleIndex > 0)
            runningReference = max(runningReference, accumulatedColor / float(sampleIndex));

        int bounceCount = diffuseCount + specularCount + transmissionCount;
        accumulatedColor += suppressFireflies(
            sampleRadiance,
            runningReference,
            bounceCount,
            diffuseCount,
            specularCount,
            transmissionCount);
    }

    // Average per-pixel over samples
    vec3 newColor = accumulatedColor / float(sceneSettings.renderSettings.samples);
    float newAlpha = float(hitAnything);
    float frameF = float(pushConstants.frame);

    vec3 prevColorPremult = prevColorData.rgb * prevColorData.a;
    float prevAlpha = prevColorData.a;

    // Apply exposure
    vec3 newColorWithExposure = newColor * exposureScale;
    vec3 newColorPremult = newColorWithExposure * newAlpha;

    // Accumulate premultiplied color
    vec3 finalColorPremult = (prevColorPremult * frameF + newColorPremult) / (frameF + 1.0);
    float finalAlpha = (prevAlpha * frameF + newAlpha) / (frameF + 1.0);

    // Un-premultiply for storage
    vec3 finalColor = (finalAlpha > 0.0) ? finalColorPremult / finalAlpha : vec3(0.0);

    // Stable auxiliary buffers from deterministic sample 0 (no temporal accumulation).
    vec3 finalAlbedo = stableAlbedo;
    vec3 finalNormal = stableNormal;

    // Store results
    imageStore(outputColor, pixelCoord, vec4(finalColor, finalAlpha));
    imageStore(outputAlbedo, pixelCoord, vec4(finalAlbedo, 1.0));
    imageStore(outputNormal, pixelCoord, vec4(finalNormal, 0.0));
}

#endif // RAY_GENERATION_GLSL
