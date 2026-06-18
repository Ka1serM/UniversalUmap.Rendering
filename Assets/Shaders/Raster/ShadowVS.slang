#include "../Shared.slang"
#include "../Bindings.slang"

struct ShadowPushConstants
{
    uint CascadeIndex;
    uint pad0;
    uint pad1;
    uint pad2;
};

[[vk::push_constant]] ConstantBuffer<ShadowPushConstants> pc;

struct VsOutput
{
    float4 Position : SV_Position;
    float2 UV : TEXCOORD0;
    float LinearDepth : TEXCOORD1;
};

float4x4 getCascadeMatrix(uint cascade)
{
    if (cascade == 0) return sceneSettings.RasterShadow.WorldToClip0;
    if (cascade == 1) return sceneSettings.RasterShadow.WorldToClip1;
    if (cascade == 2) return sceneSettings.RasterShadow.WorldToClip2;
    return sceneSettings.RasterShadow.WorldToClip3;
}

[shader("vertex")]
VsOutput main(uint vertexId : SV_VulkanVertexID, uint instanceId : SV_VulkanInstanceID)
{
    VsOutput output;

    uint sceneInstanceId = visibleInstanceIds[instanceId];
    InstanceGpu inst = instances[sceneInstanceId];
    MeshAddressesGpu mesh = meshes[inst.MeshId];

    uint vi = reinterpret<IndexBuffer*>(mesh.IndexAddress)->data[vertexId];
    Vertex vert = reinterpret<VertexBuffer*>(mesh.VertexAddress)->data[vi];
    float3 worldPos = mul(float4(vert.Position, 1.0), inst.Transform).xyz;

    float4 clip = mul(float4(worldPos, 1.0), getCascadeMatrix(pc.CascadeIndex));
    output.Position = clip;
    output.UV = vert.UV;
    output.LinearDepth = saturate(clip.z / max(clip.w, 1.0e-6));
    return output;
}
