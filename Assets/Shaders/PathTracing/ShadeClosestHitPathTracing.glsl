#ifndef CLOSEST_HIT_PT
#define CLOSEST_HIT_PT

#include "ShadeClosestHitCommon.glsl"

// Full path-tracing closest hit: implements recursive/material logic.
void shadeClosestHit(
    in vec3 worldPosition,
    in vec3 shadowPosition,
    in vec3 shadingNormal,
    in vec3 geometricNormal,
    in vec3 interpolatedTangent,
    in vec2 interpolatedUV,
    in vec3 worldRayDirection,
    in MaterialData material,
    inout Payload payload)
{
    payload.position = worldPosition;
    payload.lastBsdfPdf = 0.0;
    payload.lastNeeLightPdf = 0.0;

    vec3 viewDir = fastNormalize(-worldRayDirection);
    vec3 geometricFacingNormal = (dot(geometricNormal, viewDir) < 0.0) ? -geometricNormal : geometricNormal;

    float opacity = material.opacity;
    if (material.opacityIndex != -1)
        opacity *= texture(textureSamplers[material.opacityIndex], interpolatedUV).a;

    if (rand(payload.rngState) > opacity) {
        payload.flags |= RAY_TRANSPARENT;
        payload.nextDirection = worldRayDirection;
        payload.position = worldPosition + worldRayDirection * computeRayOriginBias(worldPosition);
        payload.attenuation = vec3(1.0);
        payload.emission = vec3(0.0);
        return;
    }

    vec3 albedo = sampleMaterialAlbedo(material, interpolatedUV);

    vec3 shadingNormalTextured = shadingNormal;
    if (material.normalIndex != -1) {
        vec3 packedNormal = texture(textureSamplers[material.normalIndex], interpolatedUV).xyz;
        shadingNormalTextured = applyNormalMap(shadingNormal, interpolatedTangent, packedNormal);
    }

    vec3 emission = material.emission * material.emissionStrength;
    if (material.emissionIndex != -1)
        emission *= texture(textureSamplers[material.emissionIndex], interpolatedUV).rgb;

    float metallic = material.metallic;
    if (material.metallicIndex != -1)
        metallic *= texture(textureSamplers[material.metallicIndex], interpolatedUV).r;

    float specular = material.specular;
    if (material.specularIndex != -1)
        specular *= texture(textureSamplers[material.specularIndex], interpolatedUV).r;
    specular = clamp(specular * 2.0, 0.0, 1.0);

    float roughness = material.roughness;
    if (material.roughnessIndex != -1)
        roughness *= texture(textureSamplers[material.roughnessIndex], interpolatedUV).r;
    roughness = clamp(roughness, 0.02, 1.0);
    roughness = regularizeSecondaryRoughness(roughness, payload.depth);

    float transmission = material.transmission;
    if (material.transmissionIndex != -1)
        transmission *= texture(textureSamplers[material.transmissionIndex], interpolatedUV).r;

    vec3 shadingFacingNormal = (dot(shadingNormalTextured, viewDir) < 0.0) ? -shadingNormalTextured : shadingNormalTextured;
    shadingFacingNormal = stabilizeShadingNormal(shadingFacingNormal, geometricFacingNormal);
    shadingFacingNormal = adaptShadingNormalLocal(viewDir, shadingFacingNormal, geometricFacingNormal);
    vec3 facingNormal = shadingFacingNormal;
    float dielectricF0 = dielectricF0FromSpecular(specular);
    vec3 F0 = mix(vec3(dielectricF0), albedo, metallic);
    float swSpecular = computeSpecularSamplingWeight(viewDir, facingNormal, albedo, F0, metallic, roughness);

    payload.albedo = albedo;
    payload.normal = facingNormal * 0.5 + 0.5;
    payload.emission = emission;

    float transmissionProbability = clamp(transmission, 0.0, 1.0);
    float opaqueProbability = max(1.0 - transmissionProbability, EPSILON);
    if (rand(payload.rngState) < transmissionProbability) {
        handleDielectricBSDF(viewDir, facingNormal, roughness, material.ior, material.transmissionColor, payload);
        payload.attenuation /= max(transmissionProbability, EPSILON);
    } else {
        payload.emission += estimateDirectLighting(
            worldPosition,
            shadowPosition,
            facingNormal,
            geometricFacingNormal,
            viewDir,
            albedo,
            specular,
            metallic,
            roughness,
            swSpecular,
            sceneSettings.environment,
            payload.rngState
        );
        handleOpaqueBSDF(viewDir, facingNormal, geometricFacingNormal, albedo, metallic, specular, roughness, payload);
        payload.attenuation /= opaqueProbability;
    }

    payload.nextDirection = fastNormalize(payload.nextDirection);
    payload.position = offsetRayOrigin(worldPosition, geometricFacingNormal, payload.nextDirection);
}

#endif
