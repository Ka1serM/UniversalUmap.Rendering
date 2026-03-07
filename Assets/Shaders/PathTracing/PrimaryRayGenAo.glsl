#ifndef PRIMARY_RAY_GEN_AO_GLSL
#define PRIMARY_RAY_GEN_AO_GLSL

#include "PrimaryRayCommon.glsl"
#include "PayloadUtils.glsl"

void primaryRayGenAo(ivec2 pixelCoord, ivec2 screenSize)
{
    if (pixelCoord.x >= screenSize.x || pixelCoord.y >= screenSize.y)
        return;

    #ifdef USE_COMPUTE
    AoPayload payload;
    #endif
    const ivec2 pickPixelCoord = ivec2(pixelCoord.x, (screenSize.y - 1) - pixelCoord.y);
    uvec2 seed = pcg2d(uvec2(pixelCoord) ^ uvec2(pushConstants.push.frame * 16777619));
    uint rngState = seed.x;

    vec3 accumulated = vec3(0.0);
    bool hitAnything = false;
    vec3 stableAlbedo = vec3(0.0);
    vec3 stableNormal = vec3(0.0);

    int spp = max(pushConstants.push.samples, 1);
    for (int sampleIndex = 0; sampleIndex < spp; ++sampleIndex) {
        if (sampleIndex == 0) {
            SamplerState stableSamplerState = initSamplerState(pixelCoord, pushConstants.push.frame + sampleIndex);
            vec3 stableRayOrigin, stableRayDirection;
            generatePrimaryRay(pixelCoord, screenSize, pushConstants.camera, stableSamplerState, true, false, stableRayOrigin, stableRayDirection);

            initializeAoPayload(payload, rngState);

            #ifdef USE_COMPUTE
                traceRayCompute(stableRayOrigin, stableRayDirection, 0.001, 1000.0, payload);
            #else
                traceRayEXT(topLevelAS, gl_RayFlagsOpaqueEXT, 0xff, 0, 0, 0, stableRayOrigin, 0.001, stableRayDirection, 1000.0, 0);
            #endif

            // Selection relies on AO modes keeping crypto/position from the stable primary hit.
            imageStore(outputCrypto, pickPixelCoord, uvec4(payload.objectIndex, 0, 0, 0));
            if (payload.objectIndex != INVALID_INSTANCE)
                imageStore(outputPosition, pickPixelCoord, vec4(payload.position, 1.0));
            else
                imageStore(outputPosition, pickPixelCoord, vec4(0));

            stableAlbedo = payload.albedo;
            stableNormal = payload.normal;
        }

        SamplerState samplerState = initSamplerState(pixelCoord, pushConstants.push.frame + sampleIndex);
        bool deterministicSample = (sampleIndex == 0);

        vec3 rayOrigin, rayDirection;
        generatePrimaryRay(pixelCoord, screenSize, pushConstants.camera, samplerState, deterministicSample, true, rayOrigin, rayDirection);

        initializeAoPayload(payload, rngState);

        #ifdef USE_COMPUTE
            traceRayCompute(rayOrigin, rayDirection, 0.001, 1000.0, payload);
        #else
            traceRayEXT(topLevelAS, gl_RayFlagsOpaqueEXT, 0xff, 0, 0, 0, rayOrigin, 0.001, rayDirection, 1000.0, 0);
        #endif

        rngState = payload.rngState;
        accumulated += payload.emission;
        hitAnything = hitAnything || ((payload.flags & ENV_TRANSPARENT) == 0u);
    }

    vec3 newColor = accumulated / float(spp);
    float newAlpha = float(hitAnything);
    float frameF = float(pushConstants.push.frame);

    vec4 prevColorData = imageLoad(outputColor, pixelCoord);
    vec3 prevColorPremult = prevColorData.rgb * prevColorData.a;
    float prevAlpha = prevColorData.a;

    vec3 newColorWithExposure = newColor * exp2(pushConstants.push.exposure);
    vec3 newColorPremult = newColorWithExposure * newAlpha;

    vec3 finalColorPremult = (prevColorPremult * frameF + newColorPremult) / (frameF + 1.0);
    float finalAlpha = (prevAlpha * frameF + newAlpha) / (frameF + 1.0);
    vec3 finalColor = (finalAlpha > 0.0) ? finalColorPremult / finalAlpha : vec3(0.0);

    imageStore(outputColor, pixelCoord, vec4(finalColor, finalAlpha));
    imageStore(outputAlbedo, pixelCoord, vec4(stableAlbedo, 1.0));
    imageStore(outputNormal, pixelCoord, vec4(stableNormal, 0.0));
}

#endif
