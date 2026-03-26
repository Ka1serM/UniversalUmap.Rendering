#include "../Shared.slang"
#include "../Bindings.slang"
#include "../Core/Core.slang"
#include "../Bxdfs/Brdf.slang"
#include "../Bxdfs/PbrBxdf.slang"
#include "../Lights/EnvironmentLight.slang"
#include "../Lights/DirectionalLight.slang"
#include "../Materials/PbrMaterial.slang"

struct VsOutput
{
    float4 Position : SV_Position;
    float3 WorldPosition : TEXCOORD0;
    float3 WorldNormal : TEXCOORD1;
    float3 WorldTangent : TEXCOORD2;
    float2 UV : TEXCOORD3;
    float TangentSign : TEXCOORD4;
    nointerpolation uint InstanceId : TEXCOORD5;
    nointerpolation uint MeshId : TEXCOORD6;
    nointerpolation uint TriangleIndex : TEXCOORD7;
};

struct FsOutput
{
    float4 Color : SV_Target0;
    float4 Albedo : SV_Target1;
    float4 Normal : SV_Target2;
    uint Crypto : SV_Target3;
    float4 Position : SV_Target4;
    float4 Adaptive : SV_Target5;
};

float3 sampleDiffuseEnvironmentRaster(float3 normal, EnvironmentDataGpu environmentData)
{
    if (environmentData.IrradianceMapIndex >= 0)
    {
        float2 uv = directionToEnvironmentUv(normal, environmentData);
        return textureSamplers[environmentData.IrradianceMapIndex].Sample(uv).rgb * environmentData.LightingExposureScale;
    }

    return sampleEnvironmentMapColorLod(
        normal,
        environmentData,
        environmentData.LightingExposureScale,
        max(environmentData.MaxTextureLod, 0.0));
}

float3 sampleSpecularEnvironmentRaster(float3 reflectedDir, float roughness, EnvironmentDataGpu environmentData)
{
    float lod = clamp(roughness, 0.0, 1.0) * max(environmentData.MaxTextureLod, 0.0);
    int specularTextureIndex = environmentData.RadianceMapIndex >= 0
        ? environmentData.RadianceMapIndex
        : environmentData.TextureIndex;
    if (specularTextureIndex < 0)
        return float3(0.0);

    float2 uv = directionToEnvironmentUv(reflectedDir, environmentData);
    return textureSamplers[specularTextureIndex].SampleLevel(uv, max(lod, 0.0)).rgb * environmentData.LightingExposureScale;
}

[shader("fragment")]
FsOutput main(VsOutput input)
{
    FsOutput output;
    MeshAddressesGpu mesh = meshes[input.MeshId];
    Face face = reinterpret<FaceBuffer*>(mesh.FaceAddress)->data[input.TriangleIndex];
    MaterialData material = reinterpret<MaterialBuffer*>(mesh.MaterialAddress)->data[face.MaterialIndex];

    float3 albedo;
    float3 emission;
    float metallic;
    float specular;
    float roughness;
    float opacity;
    float transmission;
    samplePbrMaterial(material, input.UV, albedo, emission, metallic, specular, roughness, opacity, transmission);
    transmission = transmission;
    if (opacity < 0.5)
        discard;

    float3 viewDir = fastNormalize(sceneSettings.RasterCamera.Position - input.WorldPosition);
    float3 geometricFacingNormal = computeFacingGeometricNormal(normalize(input.WorldNormal), viewDir);
    float3 shadingNormal = sampleMaterialShadingNormal(
        material.NormalIndex,
        input.UV,
        normalize(input.WorldNormal),
        normalize(input.WorldTangent),
        input.TangentSign);
    float3 facingNormal = computeFacingShadingNormal(shadingNormal, viewDir, geometricFacingNormal);

    float3 directSun = float3(0.0);
    if (hasDirectionalLight(sceneSettings.Environment))
    {
        float3 sunDir = getDirectionalLightDirection(sceneSettings.Environment);
        float nDotL = max(dot(geometricFacingNormal, sunDir), 0.0);
        if (nDotL > 0.0)
        {
            PbrBxdf bxdf;
            BxdfEval bxdfEval = bxdf.evalOpaque(
                viewDir,
                facingNormal,
                geometricFacingNormal,
                sunDir,
                albedo,
                specular,
                metallic,
                roughness,
                computeSpecularSamplingWeight(viewDir, facingNormal, albedo, lerp(float3(dielectricF0FromSpecular(specular)), albedo, metallic), metallic, roughness));
            directSun = bxdfEval.f * float3(sceneSettings.Environment.DirectionalIntensity) * nDotL;
        }
    }

    float3 diffuseEnvironment = sampleDiffuseEnvironmentRaster(facingNormal, sceneSettings.Environment);
    float3 reflectedDir = reflect(-viewDir, facingNormal);
    float3 specularEnvironment = sampleSpecularEnvironmentRaster(reflectedDir, roughness, sceneSettings.Environment);
    float dielectricF0 = dielectricF0FromSpecular(specular);
    float3 F0 = lerp(float3(dielectricF0), albedo, metallic);
    float nDotV = max(dot(facingNormal, viewDir), 0.0);
    float3 diffuseColor = albedo * (1.0 - metallic);
    float3 diffuseAmbient = (diffuseEnvironment * diffuseColor) * (1.0 / PI);
    float3 specularAmbient = specularEnvironment * envBrdfApprox(F0, roughness, nDotV);

    float3 finalColor = emission + directSun + diffuseAmbient + specularAmbient;

    output.Color = float4(finalColor, opacity);
    output.Albedo = float4(albedo, 1.0);
    output.Normal = float4(facingNormal * 0.5 + 0.5, 0.0);
    output.Crypto = input.InstanceId;
    output.Position = float4(input.WorldPosition, 1.0);
    output.Adaptive = float4(directSun, 0.0);
    return output;
}
