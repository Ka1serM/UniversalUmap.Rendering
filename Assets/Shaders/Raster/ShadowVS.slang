#include "../Shared.slang"
#include "../Bindings.slang"

[[vk::binding(11, 0)]] StructuredBuffer<uint, Std430DataLayout> visibleInstanceIds;

struct RasterPushConstants
{
    uint MeshId;
    uint VisibleOffset;
    uint CascadeIndex;
    uint VirtualPagePacked;
};

[[vk::push_constant]] ConstantBuffer<RasterPushConstants> rasterPushConstants;

struct VsOutput
{
    float4 Position : SV_Position;
    float2 UV : TEXCOORD0;
    float ShadowDepth : TEXCOORD1;
    nointerpolation uint MeshId : TEXCOORD2;
    nointerpolation uint TriangleIndex : TEXCOORD3;
};

float4x4 selectShadowWorldToClip(uint cascadeIndex)
{
    if (cascadeIndex == 0u) return sceneSettings.RasterShadow.WorldToClip0;
    if (cascadeIndex == 1u) return sceneSettings.RasterShadow.WorldToClip1;
    if (cascadeIndex == 2u) return sceneSettings.RasterShadow.WorldToClip2;
    return sceneSettings.RasterShadow.WorldToClip3;
}

float4 remapClipToVirtualShadowPage(float4 clip, uint virtualPagePacked)
{
    const float virtualPagesPerSide = 32.0;

    uint virtualPageX = virtualPagePacked & 0xffffu;
    uint virtualPageY = (virtualPagePacked >> 16u) & 0xffffu;
    float2 pageCenterNdc = (float2(float(virtualPageX), float(virtualPageY)) + 0.5) *
        (2.0 / virtualPagesPerSide) - 1.0;

    clip.xy = clip.xy * virtualPagesPerSide - clip.w * pageCenterNdc * virtualPagesPerSide;
    return clip;
}

[shader("vertex")]
VsOutput main(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
{
    VsOutput output;

    uint sceneInstanceId = visibleInstanceIds[rasterPushConstants.VisibleOffset + instanceId];
    InstanceGpu instanceData = instances[sceneInstanceId];
    MeshAddressesGpu mesh = meshes[rasterPushConstants.MeshId];

    uint vertexIndex = reinterpret<IndexBuffer*>(mesh.IndexAddress)->data[vertexId];
    Vertex vertex = reinterpret<VertexBuffer*>(mesh.VertexAddress)->data[vertexIndex];

    float3 worldPosition = mul(float4(vertex.Position, 1.0), instanceData.Transform).xyz;
    float4 clip = mul(float4(worldPosition, 1.0), selectShadowWorldToClip(rasterPushConstants.CascadeIndex));
    clip = remapClipToVirtualShadowPage(clip, rasterPushConstants.VirtualPagePacked);

    output.Position = clip;
    output.UV = vertex.UV;
    output.ShadowDepth = saturate(clip.z / max(clip.w, 1.0e-6));
    output.MeshId = rasterPushConstants.MeshId;
    output.TriangleIndex = vertexId / 3u;
    return output;
}
