#ifndef CLOSEST_HIT_AO
#define CLOSEST_HIT_AO

#include "ShadeClosestHitCommon.glsl"

// AO-only closest hit: writes AO/G-buffer information and terminates.
void shadeClosestHit(
    in vec3 worldPosition,
    in vec3 shadowPosition,
    in vec3 shadingNormal,
    in vec3 geometricNormal,
    in vec3 interpolatedTangent,
    in vec2 interpolatedUV,
    in vec3 worldRayDirection,
    in MaterialData material,
    inout AoPayload payload)
{
    vec3 viewDir = fastNormalize(-worldRayDirection);
    vec3 geometricFacingNormal = (dot(geometricNormal, viewDir) < 0.0) ? -geometricNormal : geometricNormal;

    payload.position = worldPosition;

    // AO pipeline: keep albedo neutral and output only ambient occlusion.
    vec3 albedo = vec3(1.0);

    vec3 shadingNormalTextured = shadingNormal;
    if (material.normalIndex != -1) {
        vec3 packedNormal = texture(textureSamplers[material.normalIndex], interpolatedUV).xyz;
        shadingNormalTextured = applyNormalMap(shadingNormal, interpolatedTangent, packedNormal);
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
