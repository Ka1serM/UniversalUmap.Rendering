#ifndef CLOSEST_HIT_DIRECT
#define CLOSEST_HIT_DIRECT

#include "ShadeClosestHitCommon.glsl"

// Direct lighting closest hit: evaluate PBR direct lighting (AO replaces GI) and terminate.
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

    // Opacity/alpha test
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

    // Direct lighting pipeline behavior (no branches)
    payload.emission = shadeAmbientOcclusionPbrSurface(
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
        emission,
        payload.rngState);
    payload.attenuation = vec3(1.0);
    payload.nextDirection = worldRayDirection;
    payload.flags |= RAY_TERMINATED;
}

#endif
