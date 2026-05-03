#include "../Shared.slang"
#include "../Bindings.slang"

[[vk::binding(11, 0)]] StructuredBuffer<uint, Std430DataLayout> visibleInstanceIds;

struct RasterPushConstants
{
    uint MeshId;
    uint VisibleOffset;
    uint Pad0;
    uint Pad1;
};

[[vk::push_constant]] ConstantBuffer<RasterPushConstants> rasterPushConstants;

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
    float3x3 normalMatrix = (float3x3)instanceData.NormalTransform;
    float3 worldNormal = normalize(mul(vertex.Normal, normalMatrix));
    float3 worldTangent = normalize(mul(vertex.Tangent, normalMatrix));

    output.Position = mul(float4(worldPosition, 1.0), sceneSettings.RasterCamera.WorldToClip);
    output.WorldPosition = worldPosition;
    output.WorldNormal = worldNormal;
    output.WorldTangent = worldTangent;
    output.UV = vertex.UV;
    output.TangentSign = vertex.TangentSign;
    output.InstanceId = sceneInstanceId;
    output.MeshId = rasterPushConstants.MeshId;
    output.TriangleIndex = vertexId / 3u;
    return output;
}
