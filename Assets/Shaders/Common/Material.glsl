#ifndef MATERIAL_GLSL
#define MATERIAL_GLSL

#include "../SharedStructs.h"
#include "../Bindings.glsl"

vec3 sampleMaterialAlbedo(MaterialData material, vec2 uv)
{
    vec3 albedo = material.albedo;
    if (material.albedoIndex != -1)
        albedo *= texture(textureSamplers[material.albedoIndex], uv).rgb;
    return albedo;
}

vec3 sampleMaterialEmission(MaterialData material, vec2 uv)
{
    vec3 emission = material.emission * material.emissionStrength;
    if (material.emissionIndex != -1)
        emission *= texture(textureSamplers[material.emissionIndex], uv).rgb;
    return emission;
}

float sampleMaterialMetallic(MaterialData material, vec2 uv)
{
    float metallic = material.metallic;
    if (material.metallicIndex != -1)
        metallic *= texture(textureSamplers[material.metallicIndex], uv).r;
    return metallic;
}

float sampleMaterialSpecular(MaterialData material, vec2 uv)
{
    float specular = material.specular;
    if (material.specularIndex != -1)
        specular *= texture(textureSamplers[material.specularIndex], uv).r;
    return specular;
}

float sampleMaterialRoughness(MaterialData material, vec2 uv)
{
    float roughness = material.roughness;
    if (material.roughnessIndex != -1)
        roughness *= texture(textureSamplers[material.roughnessIndex], uv).r;
    return roughness;
}

float sampleMaterialOpacity(MaterialData material, vec2 uv)
{
    float opacity = material.opacity;
    if (material.opacityIndex != -1)
        opacity *= texture(textureSamplers[material.opacityIndex], uv).a;
    return opacity;
}

float sampleMaterialTransmission(MaterialData material, vec2 uv)
{
    float transmission = material.transmission;
    if (material.transmissionIndex != -1)
        transmission *= texture(textureSamplers[material.transmissionIndex], uv).r;
    return transmission;
}

#endif // MATERIAL_GLSL
