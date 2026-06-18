#include "../Shared.slang"
#include "../Bindings.slang"

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

    float3x3 normalMat = (float3x3)inst.NormalTransform;
    float3 worldNormal = normalize(mul(vert.Normal, normalMat));
    float3 worldTangent = normalize(mul(vert.Tangent, normalMat));

    float4 clip = mul(float4(worldPos, 1.0), sceneSettings.RasterCamera.WorldToClip);
    float4 prevClip = mul(float4(worldPos, 1.0), sceneSettings.RasterCamera.PrevWorldToClip);
    clip.xy += sceneSettings.RasterCamera.JitterNdc * clip.w;
    prevClip.xy += sceneSettings.RasterCamera.PrevJitterNdc * prevClip.w;

    output.Position = clip;
    output.PrevClipPosition = prevClip;
    output.WorldPosition = worldPos;
    output.WorldNormal = worldNormal;
    output.WorldTangent = worldTangent;
    output.UV = vert.UV;
    output.TangentSign = vert.TangentSign;
    output.InstanceId = sceneInstanceId;
    output.MeshId = inst.MeshId;
    output.TriangleIndex = vertexId / 3u;
    return output;
}
