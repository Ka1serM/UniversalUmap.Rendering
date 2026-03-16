#ifndef DIRECTLIGHTING_COMMON_GLSL
#define DIRECTLIGHTING_COMMON_GLSL

#include "../Bindings.glsl"
#include "../Common.glsl"
#include "../Common/Intersection.glsl"
#include "../Common/Miss.glsl"
#include "../Common/Brdf.glsl"
#include "../Common/Material.glsl"
#include "../AO/AoCommon.glsl"

vec3 evaluateOpaqueBSDFAndPdf(
    vec3 viewDir,
    vec3 perturbedNormal,
    vec3 geometricNormal,
    vec3 lightDir,
    vec3 albedo,
    float specular,
    float metallic,
    float roughness,
    float swSpecular,
    out float bsdfPdf)
{
    float NdotLGeom = max(dot(geometricNormal, lightDir), 0.0);
    if (NdotLGeom <= 0.0) {
        bsdfPdf = 0.0;
        return vec3(0.0);
    }

    vec3 N = perturbedNormal;
    vec3 V = viewDir;
    vec3 L = lightDir;
    vec3 H = normalize(V + L);

    float NdotL = max(dot(N, L), 0.0);
    float NdotV = max(dot(N, V), 0.0);
    float NdotH = max(dot(N, H), 0.0);
    float VdotH = max(dot(V, H), 0.0);

    if (NdotL <= 0.0 || NdotV <= 0.0) {
        bsdfPdf = 0.0;
        return vec3(0.0);
    }

    float dielectricF0 = dielectricF0FromSpecular(specular);
    vec3 F0 = mix(vec3(dielectricF0), albedo, metallic);
    vec3 F = fresnelSchlick(F0, VdotH);

    vec3 diffuseBRDF = (vec3(1.0) - F) * evaluateDiffuseBRDF(albedo, metallic);
    vec3 specularBRDF = evaluateSpecularBRDF(N, V, L, F, roughness, H);

    float specPdf = pdfSpecular(V, N, H, roughness);
    float diffPdf = pdfDiffuse(N, L);
    bsdfPdf = max(swSpecular * specPdf + (1.0 - swSpecular) * diffPdf, EPSILON);

    return diffuseBRDF + specularBRDF;
}

vec3 estimateDirectLighting(
    vec3 worldPosition,
    vec3 shadowPosition,
    vec3 shadingNormal,
    vec3 geometricNormal,
    vec3 viewDir,
    vec3 albedo,
    float specular,
    float metallic,
    float roughness,
    float swSpecular,
    in EnvironmentDataGpu environmentData,
    inout uint rngState)
{
    vec3 directContribution = vec3(0.0);

    if (hasDirectionalLight(environmentData)) {
        vec3 sunDir = getDirectionalLightDirection(environmentData);
        float NdotLGeomSun = max(dot(geometricNormal, sunDir), 0.0);
        if (NdotLGeomSun > 0.0) {
            float shadowBiasSun = computeShadowBias(worldPosition, NdotLGeomSun);
            vec3 shadowOriginSun = shadowPosition + geometricNormal * shadowBiasSun;
            if (!traceShadowRay(shadowOriginSun, sunDir, shadowBiasSun, 1000.0)) {
                float bsdfPdfSun = 0.0;
                vec3 bsdfSun = evaluateOpaqueBSDFAndPdf(
                    viewDir, shadingNormal, geometricNormal, sunDir,
                    albedo, specular, metallic, roughness, swSpecular, bsdfPdfSun);
                directContribution += bsdfSun * vec3(environmentData.directionalIntensity) * NdotLGeomSun;
            }
        }
    }

    if (environmentData.textureIndex < 0 || environmentData.cdfTextureIndex < 0)
        return directContribution;

    float lightPdf = 0.0;
    vec3 lightDir = sampleEnvironmentDirection(environmentData, rngState, lightPdf);
    vec3 Li = sampleEnvironmentMapColor(lightDir, environmentData, environmentData.lightingExposureScale);
    float NdotLGeom = max(dot(geometricNormal, lightDir), 0.0);
    if (NdotLGeom <= 0.0 || lightPdf <= 0.0)
        return directContribution;

    float shadowBias = computeShadowBias(worldPosition, NdotLGeom);
    vec3 shadowOrigin = shadowPosition + geometricNormal * shadowBias;
    if (traceShadowRay(shadowOrigin, lightDir, shadowBias, 1000.0))
        return directContribution;

    float bsdfPdf = 0.0;
    vec3 bsdf = evaluateOpaqueBSDFAndPdf(
        viewDir, shadingNormal, geometricNormal, lightDir,
        albedo, specular, metallic, roughness, swSpecular, bsdfPdf);
    if (bsdfPdf <= 0.0)
        return directContribution;

    float wLight = powerHeuristic(lightPdf, bsdfPdf);
    directContribution += bsdf * Li * (NdotLGeom * wLight / max(lightPdf, EPSILON));
    return directContribution;
}

vec3 sampleDiffuseEnvironment(
    vec3 normal,
    EnvironmentDataGpu environmentData)
{
    if (environmentData.irradianceMapIndex >= 0) {
        vec2 uv = directionToEnvironmentUv(normal, environmentData);
        return texture(textureSamplers[environmentData.irradianceMapIndex], uv).rgb
             * environmentData.lightingExposureScale;
    }

    // Fallback when no irradiance map exists: use a heavily blurred env mip.
    return sampleEnvironmentMapColorLod(
        normal,
        environmentData,
        environmentData.lightingExposureScale,
        max(environmentData.maxTextureLod, 0.0));
}

vec3 sampleSpecularEnvironment(
    vec3 reflectedDir,
    float roughness,
    EnvironmentDataGpu environmentData)
{
    float perceptualRoughness = clamp(roughness, 0.0, 1.0);
    float lod = perceptualRoughness * max(environmentData.maxTextureLod, 0.0);
    int specularTextureIndex = environmentData.radianceMapIndex >= 0
        ? environmentData.radianceMapIndex
        : environmentData.textureIndex;
    if (specularTextureIndex < 0)
        return vec3(1.0);

    vec2 uv = directionToEnvironmentUv(reflectedDir, environmentData);
    return textureLod(textureSamplers[specularTextureIndex], uv, max(lod, 0.0)).rgb
         * environmentData.lightingExposureScale;
}

vec3 shadeAmbientOcclusionPbrSurface(
    in vec3 worldPosition,
    in vec3 shadowPosition,
    in vec3 shadingFacingNormal,
    in vec3 geometricFacingNormal,
    in vec3 viewDir,
    in vec3 albedo,
    in float specular,
    in float metallic,
    in float roughness,
    in float swSpecular,
    in vec3 baseEmission,
    in EnvironmentDataGpu environmentData,
    inout uint rngState)
{
    float ao = clamp(estimateAmbientOcclusion(worldPosition, geometricFacingNormal, rngState), 0.0, 1.0);

    vec3 directLighting = estimateDirectLighting(
        worldPosition,
        shadowPosition,
        shadingFacingNormal,
        geometricFacingNormal,
        viewDir,
        albedo,
        specular,
        metallic,
        roughness,
        swSpecular,
        environmentData,
        rngState);

    vec3 diffuseEnvironment = sampleDiffuseEnvironment(shadingFacingNormal, environmentData);

    vec3 reflectedDir = reflect(-viewDir, shadingFacingNormal);
    vec3 specularEnvironment = sampleSpecularEnvironment(reflectedDir, roughness, environmentData);

    float dielectricF0 = dielectricF0FromSpecular(specular);
    vec3 F0 = mix(vec3(dielectricF0), albedo, metallic);
    float NdotV = max(dot(shadingFacingNormal, viewDir), 0.0);

    vec3 diffuseColor = albedo * (1.0 - metallic);
    vec3 diffuseAmbient = (diffuseEnvironment * diffuseColor) * (1.0 / PI);
    vec3 specularAmbient = specularEnvironment * envBrdfApprox(F0, clamp(roughness, 0.0, 1.0), NdotV);
    float specularAo = computeSpecularOcclusion(NdotV, ao, clamp(roughness, 0.0, 1.0));
    specularAmbient *= specularAo;

    // AO affects the entire non-emissive lighting term in direct-light mode.
    vec3 directWithAo = directLighting * ao;
    vec3 ambientWithAo = (diffuseAmbient * ao) + (specularAmbient * ao);
    return baseEmission + directWithAo + ambientWithAo;
}

void shadePrimaryMissDirect(in vec3 worldRayDirection, inout Payload payload)
{
    shadeMiss(
        worldRayDirection,
        sceneSettings.environment,
        sceneSettings.renderSettings,
        payload);
}

void shadeClosestHitDirect(
    in vec3 worldPosition,
    in vec3 shadowPosition,
    in vec3 shadingNormal,
    in vec3 geometricNormal,
    in vec3 interpolatedTangent,
    in float interpolatedTangentSign,
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

    float opacity = sampleMaterialOpacity(material, interpolatedUV);

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
        shadingNormalTextured = applyNormalMap(shadingNormal, interpolatedTangent, interpolatedTangentSign, packedNormal);
    }

    vec3 emission = sampleMaterialEmission(material, interpolatedUV);
    float metallic = sampleMaterialMetallic(material, interpolatedUV);
    float specular = sampleMaterialSpecular(material, interpolatedUV);
    specular = clamp(specular * 2.0, 0.0, 1.0);

    float roughness = sampleMaterialRoughness(material, interpolatedUV);
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
        sceneSettings.environment,
        payload.rngState);

    payload.attenuation = vec3(1.0);
    payload.nextDirection = worldRayDirection;
    payload.flags |= RAY_TERMINATED;
}

#endif // DIRECTLIGHTING_COMMON_GLSL
