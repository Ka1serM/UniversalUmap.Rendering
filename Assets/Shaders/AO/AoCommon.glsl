#ifndef AO_COMMON_GLSL
#define AO_COMMON_GLSL

#include "../SharedStructs.h"
#include "../Bindings.glsl"
#include "../Common.glsl"
#include "../Common/Brdf.glsl"
#include "../Common/Miss.glsl"

const float AO_MAX_DISTANCE = 1.5;
const int AO_SAMPLES = 4;

bool traceAoShadowRay(vec3 rayOrigin, vec3 rayDirection, float tMin, float tMax)
{
#ifdef USE_COMPUTE
    return traceShadowRay(rayOrigin, rayDirection, tMin, tMax);
#else
    shadowPayload = 1u;
    const uint shadowFlags = gl_RayFlagsTerminateOnFirstHitEXT
        | gl_RayFlagsOpaqueEXT
        | gl_RayFlagsSkipClosestHitShaderEXT;
    traceRayEXT(topLevelAS, shadowFlags, 0xFF, 0, 0, 1, rayOrigin, tMin, rayDirection, tMax, 1);
    return shadowPayload != 0u;
#endif
}

float estimateAmbientOcclusion(vec3 worldPosition, vec3 geometricNormal, inout uint rngState)
{
    float occluded = 0.0;
    for (int i = 0; i < AO_SAMPLES; ++i) {
        vec3 dir = sampleDiffuse(geometricNormal, rngState);
        float nDotL = max(dot(geometricNormal, dir), 0.0);

        float bias = computeShadowBias(worldPosition, nDotL);
        vec3 origin = worldPosition + geometricNormal * bias;
        if (traceAoShadowRay(origin, dir, bias, AO_MAX_DISTANCE))
            occluded += 1.0;
    }
    return 1.0 - (occluded / float(AO_SAMPLES));
}

vec3 shadeAmbientOcclusionOnly(float ao)
{
    return vec3(ao);
}

vec3 shadeAmbientOcclusionWithAlbedo(vec3 albedo, float ao)
{
    return albedo * ao;
}

vec3 shadeAmbientOcclusionSurface(vec3 worldPosition, vec3 geometricNormal, inout uint rngState)
{
    float ao = estimateAmbientOcclusion(worldPosition, geometricNormal, rngState);
    return shadeAmbientOcclusionOnly(ao);
}

#ifdef USE_COMPUTE
void shadeClosestHitAo(
    in vec3 worldPosition,
    in vec3 shadingNormal,
    in vec3 geometricNormal,
    in vec3 interpolatedTangent,
    in float interpolatedTangentSign,
    in vec2 interpolatedUV,
    in vec3 worldRayDirection,
    in MaterialData material,
    inout AoPayload payload)
{
    vec3 viewDir = fastNormalize(-worldRayDirection);
    vec3 geometricFacingNormal = (dot(geometricNormal, viewDir) < 0.0) ? -geometricNormal : geometricNormal;

    payload.position = worldPosition;
    vec3 albedo = vec3(1.0);

    vec3 shadingNormalTextured = shadingNormal;
    if (material.normalIndex != -1) {
        vec3 packedNormal = texture(textureSamplers[material.normalIndex], interpolatedUV).xyz;
        shadingNormalTextured = applyNormalMap(shadingNormal, interpolatedTangent, interpolatedTangentSign, packedNormal);
    }

    vec3 shadingFacingNormal = (dot(shadingNormalTextured, viewDir) < 0.0) ? -shadingNormalTextured : shadingNormalTextured;
    shadingFacingNormal = stabilizeShadingNormal(shadingFacingNormal, geometricFacingNormal);
    shadingFacingNormal = adaptShadingNormalLocal(viewDir, shadingFacingNormal, geometricFacingNormal);

    payload.albedo = albedo;
    payload.normal = shadingFacingNormal * 0.5 + 0.5;
    payload.emission = shadeAmbientOcclusionSurface(worldPosition, geometricFacingNormal, payload.rngState);

    payload.flags |= RAY_TERMINATED;
}
#endif

void shadeMissAo(in vec3 worldRayDirection, inout AoPayload payload)
{
    shadeMiss(
        worldRayDirection,
        sceneSettings.environment,
        sceneSettings.renderSettings,
        payload);
}

#endif // AO_COMMON_GLSL
