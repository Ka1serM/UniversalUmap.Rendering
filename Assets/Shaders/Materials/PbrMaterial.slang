#ifndef PBR_MATERIAL_SLANG
#define PBR_MATERIAL_SLANG

#include "../Bindings.slang"

float3 sampleMaterialAlbedo(MaterialData material, float2 uv)
{
    float3 albedo = material.Albedo;
    if (material.AlbedoIndex != -1)
        albedo *= textureSamplers[material.AlbedoIndex].Sample(uv).rgb;
    return albedo;
}

float3 sampleMaterialEmission(MaterialData material, float2 uv)
{
    float3 emission = material.Emission * material.EmissionStrength;
    if (material.EmissionIndex != -1)
        emission *= textureSamplers[material.EmissionIndex].Sample(uv).rgb;
    return emission;
}

float sampleMaterialMetallic(MaterialData material, float2 uv)
{
    float metallic = material.Metallic;
    if (material.MetallicIndex != -1)
        metallic *= textureSamplers[material.MetallicIndex].Sample(uv).r;
    return metallic;
}

float sampleMaterialSpecular(MaterialData material, float2 uv)
{
    float specular = material.Specular;
    if (material.SpecularIndex != -1)
        specular *= textureSamplers[material.SpecularIndex].Sample(uv).r;
    return specular;
}

float sampleMaterialRoughness(MaterialData material, float2 uv)
{
    float roughness = material.Roughness;
    if (material.RoughnessIndex != -1)
        roughness *= textureSamplers[material.RoughnessIndex].Sample(uv).r;
    return roughness;
}

float sampleMaterialOpacity(MaterialData material, float2 uv)
{
    float opacity = material.Opacity;
    if (material.OpacityIndex != -1)
        opacity *= textureSamplers[material.OpacityIndex].Sample(uv).a;
    return opacity;
}

float sampleMaterialTransmission(MaterialData material, float2 uv)
{
    float transmission = material.Transmission;
    if (material.TransmissionIndex != -1)
        transmission *= textureSamplers[material.TransmissionIndex].Sample(uv).r;
    return transmission;
}

void sampleMaterialPbrInputs(
    in MaterialData material,
    in float2 uv,
    out float3 albedo,
    out float3 emission,
    out float metallic,
    out float specular,
    out float roughness)
{
    albedo = sampleMaterialAlbedo(material, uv);
    emission = sampleMaterialEmission(material, uv);
    metallic = sampleMaterialMetallic(material, uv);
    specular = clamp(sampleMaterialSpecular(material, uv) * 2.0, 0.0, 1.0);
    roughness = clamp(sampleMaterialRoughness(material, uv), 0.02, 1.0);
}

void samplePbrMaterial(
    in MaterialData material,
    in float2 uv,
    out float3 albedo,
    out float3 emission,
    out float metallic,
    out float specular,
    out float roughness,
    out float opacity,
    out float transmission)
{
    sampleMaterialPbrInputs(material, uv, albedo, emission, metallic, specular, roughness);
    opacity = sampleMaterialOpacity(material, uv);
    transmission = sampleMaterialTransmission(material, uv);
}

#endif // PBR_MATERIAL_SLANG
