#include "../Shared.slang"
#include "../Bindings.slang"
#include "../Core/Core.slang"
#include "../Bxdfs/Brdf.slang"
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
    float4 PrevClipPosition : TEXCOORD8;
};

struct FsOutput
{
    float4 Color : SV_Target0;
    float4 Albedo : SV_Target1;
    float4 Normal : SV_Target2;
    uint Crypto : SV_Target3;
    float4 Position : SV_Target4;
    float4 Material : SV_Target5;
    float2 Velocity : SV_Target6;
};

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

    if (opacity < 0.5) discard;

    float3 viewDir = fastNormalize(sceneSettings.RasterCamera.Position - input.WorldPosition);
    float3 geometricNormal = computeFacingGeometricNormal(normalize(input.WorldNormal), viewDir);
    float3 shadingNormal = sampleMaterialShadingNormal(
        material.NormalIndex, input.UV,
        normalize(input.WorldNormal), normalize(input.WorldTangent), input.TangentSign);
    float3 facingNormal = computeFacingShadingNormal(shadingNormal, viewDir, geometricNormal);

    output.Color = float4(emission, opacity);
    output.Albedo = float4(albedo, 1.0);
    output.Normal = float4(facingNormal * 0.5 + 0.5, 0.0);
    output.Crypto = input.InstanceId;
    output.Position = float4(input.WorldPosition, 1.0);
    output.Material = float4(metallic, specular, roughness, transmission);

    float2 currNdc = input.Position.xy / max(input.Position.w, 1.0e-6);
    float2 prevNdc = input.PrevClipPosition.xy / max(input.PrevClipPosition.w, 1.0e-6);
    output.Velocity = (prevNdc - currNdc) * 0.5;

    return output;
}
