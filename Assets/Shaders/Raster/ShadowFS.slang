#include "../Shared.slang"
#include "../Bindings.slang"
#include "../Materials/PbrMaterial.slang"

static const float SHADOW_MOMENT_CONSTANT_DEPTH_BIAS = 0.00015;
static const float SHADOW_MOMENT_SLOPE_DEPTH_BIAS = 2.0;

struct VsOutput
{
    float4 Position : SV_Position;
    float2 UV : TEXCOORD0;
    float ShadowDepth : TEXCOORD1;
    nointerpolation uint MeshId : TEXCOORD2;
    nointerpolation uint TriangleIndex : TEXCOORD3;
};

[shader("fragment")]
float4 main(VsOutput input) : SV_Target0
{
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
    if (opacity < 0.5)
        discard;

    float slope = max(abs(ddx(input.ShadowDepth)), abs(ddy(input.ShadowDepth)));
    float depth = saturate(input.ShadowDepth + SHADOW_MOMENT_CONSTANT_DEPTH_BIAS + slope * SHADOW_MOMENT_SLOPE_DEPTH_BIAS);
    float depth2 = depth * depth;
    return float4(depth, depth2, depth2 * depth, depth2 * depth2);
}
