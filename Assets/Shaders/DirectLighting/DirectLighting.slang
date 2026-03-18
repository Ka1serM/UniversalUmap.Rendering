#ifndef DIRECTLIGHTING_SLANG
#define DIRECTLIGHTING_SLANG

#include "../Bindings.slang"
#include "../Core/Core.slang"
#include "../Bxdfs/Brdf.slang"
#include "../Bxdfs/PbrBxdf.slang"
#include "../Lights/DirectionalLight.slang"
#include "../Lights/EnvironmentLight.slang"
#include "../AO/AmbientOcclusion.slang"

float3 estimateDirectLighting(
    float3 worldPosition,
    float3 shadowPosition,
    float3 shadingNormal,
    float3 geometricNormal,
    float3 viewDir,
    float3 albedo,
    float specular,
    float metallic,
    float roughness,
    float swSpecular,
    in EnvironmentDataGpu environmentData,
    inout uint rngState)
{
    float3 directContribution = float3(0.0);

    if (hasDirectionalLight(environmentData)) {
        DirectionalLight directionalLight;
        LightLiSample sunSample = directionalLight.sampleLi(worldPosition, float2(0.0));
        float3 sunDir = sunSample.wi;
        float NdotLGeomSun = max(dot(geometricNormal, sunDir), 0.0);
        if (NdotLGeomSun > 0.0) {
            float shadowBiasSun = computeShadowBias(worldPosition, NdotLGeomSun) * 2.0;
            float3 shadowOriginSun = worldPosition + geometricNormal * shadowBiasSun;
            if (!traceShadowRay(shadowOriginSun, sunDir, shadowBiasSun, 1000.0)) {
                PbrBxdf bxdf;
                BxdfEval bxdfSun = bxdf.evalOpaque(
                    viewDir, shadingNormal, geometricNormal, sunDir,
                    albedo, specular, metallic, roughness, swSpecular);
                directContribution += bxdfSun.f * sunSample.Li * NdotLGeomSun;
            }
        }
    }

    if (environmentData.textureIndex < 0 || environmentData.cdfTextureIndex < 0)
        return directContribution;

    EnvironmentLight environmentLight;
    LightLiSample lightSample = environmentLight.sampleLi(worldPosition, float2(rand(rngState), rand(rngState)));
    float lightPdf = lightSample.pdf;
    float3 lightDir = lightSample.wi;
    float3 Li = lightSample.Li;
    float NdotLGeom = max(dot(geometricNormal, lightDir), 0.0);
    if (NdotLGeom <= 0.0 || lightPdf <= 0.0)
        return directContribution;

    float shadowBias = computeShadowBias(worldPosition, NdotLGeom);
    float3 shadowOrigin = shadowPosition + geometricNormal * shadowBias;
    if (traceShadowRay(shadowOrigin, lightDir, shadowBias, 1000.0))
        return directContribution;

    PbrBxdf bxdf;
    BxdfEval bxdfEval = bxdf.evalOpaque(
        viewDir, shadingNormal, geometricNormal, lightDir,
        albedo, specular, metallic, roughness, swSpecular);
    if (bxdfEval.pdf <= 0.0)
        return directContribution;

    float wLight = powerHeuristic(lightPdf, bxdfEval.pdf);
    directContribution += bxdfEval.f * Li * (NdotLGeom * wLight / max(lightPdf, EPSILON));
    return directContribution;
}

float3 sampleDiffuseEnvironment(
    float3 normal,
    EnvironmentDataGpu environmentData)
{
    if (environmentData.irradianceMapIndex >= 0) {
        float2 uv = directionToEnvironmentUv(normal, environmentData);
        return textureSamplers[environmentData.irradianceMapIndex].Sample(uv).rgb
             * environmentData.lightingExposureScale;
    }

    return sampleEnvironmentMapColorLod(
        normal,
        environmentData,
        environmentData.lightingExposureScale,
        max(environmentData.maxTextureLod, 0.0));
}

float3 sampleSpecularEnvironment(
    float3 reflectedDir,
    float roughness,
    EnvironmentDataGpu environmentData)
{
    float perceptualRoughness = clamp(roughness, 0.0, 1.0);
    float lod = perceptualRoughness * max(environmentData.maxTextureLod, 0.0);
    int specularTextureIndex = environmentData.radianceMapIndex >= 0
        ? environmentData.radianceMapIndex
        : environmentData.textureIndex;
    if (specularTextureIndex < 0)
        return float3(1.0);

    float2 uv = directionToEnvironmentUv(reflectedDir, environmentData);
    return textureSamplers[specularTextureIndex].SampleLevel(uv, max(lod, 0.0)).rgb
         * environmentData.lightingExposureScale;
}

float3 shadeAmbientOcclusionPbrSurface(
    in float3 worldPosition,
    in float3 shadowPosition,
    in float3 shadingFacingNormal,
    in float3 geometricFacingNormal,
    in float3 viewDir,
    in float3 albedo,
    in float specular,
    in float metallic,
    in float roughness,
    in float swSpecular,
    in float3 baseEmission,
    in EnvironmentDataGpu environmentData,
    inout uint rngState)
{
    float ao = clamp(estimateAmbientOcclusion(worldPosition, geometricFacingNormal, rngState), 0.0, 1.0);

    float3 directLighting = estimateDirectLighting(
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

    float3 diffuseEnvironment = sampleDiffuseEnvironment(shadingFacingNormal, environmentData);

    float3 reflectedDir = reflect(-viewDir, shadingFacingNormal);
    float3 specularEnvironment = sampleSpecularEnvironment(reflectedDir, roughness, environmentData);

    float dielectricF0 = dielectricF0FromSpecular(specular);
    float3 F0 = lerp(float3(dielectricF0), albedo, metallic);
    float NdotV = max(dot(shadingFacingNormal, viewDir), 0.0);
    float perceptualRoughness = clamp(roughness, 0.0, 1.0);

    float3 diffuseColor = albedo * (1.0 - metallic);
    float3 diffuseAmbient = (diffuseEnvironment * diffuseColor) * (1.0 / PI);
    float3 specularAmbient = specularEnvironment * envBrdfApprox(F0, perceptualRoughness, NdotV);
    float specularAo = computeSpecularOcclusion(NdotV, ao, perceptualRoughness);
    specularAmbient *= specularAo;

    float3 directWithAo = directLighting * ao;
    float3 ambientWithAo = (diffuseAmbient * ao) + (specularAmbient * ao);
    return baseEmission + directWithAo + ambientWithAo;
}

#endif // DIRECTLIGHTING_SLANG
