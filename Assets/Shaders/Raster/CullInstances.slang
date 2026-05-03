#include "../Shared.slang"
#include "../Bindings.slang"

[[vk::binding(10, 0)]] StructuredBuffer<MeshRasterMetadataGpu, Std430DataLayout> rasterMeshes;
[[vk::binding(11, 0)]] RWStructuredBuffer<uint> visibleInstanceIds;
[[vk::binding(12, 0)]] RWStructuredBuffer<uint> visibleCounts;
[[vk::binding(14, 0)]] Sampler2D previousPositionTexture;

struct CullPushConstants
{
    int HasHistory;
    int IsMoving;
    int Pad0;
    int Pad1;
};

[[vk::push_constant]] ConstantBuffer<CullPushConstants> cullPushConstants;

float4 transformLocalCorner(float3 localBoundsMin, float3 localBoundsMax, uint cornerIndex, float4x4 transform)
{
    float3 localPosition = float3(
        (cornerIndex & 1u) != 0u ? localBoundsMax.x : localBoundsMin.x,
        (cornerIndex & 2u) != 0u ? localBoundsMax.y : localBoundsMin.y,
        (cornerIndex & 4u) != 0u ? localBoundsMax.z : localBoundsMin.z);
    float3 worldPosition = mul(float4(localPosition, 1.0), transform).xyz;
    return mul(float4(worldPosition, 1.0), sceneSettings.RasterCamera.WorldToClip);
}

bool isOutsideFrustum(InstanceGpu instanceData, MeshAddressesGpu mesh)
{
    uint outsideLeft = 0u;
    uint outsideRight = 0u;
    uint outsideBottom = 0u;
    uint outsideTop = 0u;
    uint outsideNear = 0u;
    uint outsideFar = 0u;

    for (uint i = 0u; i < 8u; i++)
    {
        float4 clip = transformLocalCorner(mesh.LocalBoundsMin, mesh.LocalBoundsMax, i, instanceData.Transform);
        outsideLeft += clip.x < -clip.w ? 1u : 0u;
        outsideRight += clip.x > clip.w ? 1u : 0u;
        outsideBottom += clip.y < -clip.w ? 1u : 0u;
        outsideTop += clip.y > clip.w ? 1u : 0u;
        outsideNear += clip.z < 0.0 ? 1u : 0u;
        outsideFar += clip.z > clip.w ? 1u : 0u;
    }

    return outsideLeft == 8u ||
        outsideRight == 8u ||
        outsideBottom == 8u ||
        outsideTop == 8u ||
        outsideNear == 8u ||
        outsideFar == 8u;
}

bool tryGetScreenBoundsAndNearestDepth(
    InstanceGpu instanceData,
    MeshAddressesGpu mesh,
    out float2 minUv,
    out float2 maxUv,
    out float nearestDepth)
{
    minUv = float2(1.0);
    maxUv = float2(0.0);
    nearestDepth = 1.0;

    bool anyInFront = false;
    for (uint i = 0u; i < 8u; i++)
    {
        float4 clip = transformLocalCorner(mesh.LocalBoundsMin, mesh.LocalBoundsMax, i, instanceData.Transform);
        if (clip.w <= 0.0001)
            continue;

        float3 ndc = clip.xyz / clip.w;
        float2 uv = ndc.xy * float2(0.5, -0.5) + float2(0.5);
        minUv = min(minUv, uv);
        maxUv = max(maxUv, uv);
        nearestDepth = min(nearestDepth, ndc.z);
        anyInFront = true;
    }

    minUv = clamp(minUv, float2(0.0), float2(1.0));
    maxUv = clamp(maxUv, float2(0.0), float2(1.0));
    return anyInFront && all(maxUv >= minUv);
}

float samplePreviousDepth(float2 uv)
{
    float4 previousPosition = previousPositionTexture.SampleLevel(uv, 0.0);
    if (previousPosition.w <= 0.5)
        return -1.0;

    float4 previousClip = mul(float4(previousPosition.xyz, 1.0), sceneSettings.RasterCamera.WorldToClip);
    if (previousClip.w <= 0.0001)
        return -1.0;

    return previousClip.z / previousClip.w;
}

bool isOccludedByHistory(InstanceGpu instanceData, MeshAddressesGpu mesh)
{
    if (cullPushConstants.HasHistory == 0 || cullPushConstants.IsMoving != 0)
        return false;

    float2 minUv;
    float2 maxUv;
    float nearestDepth;
    if (!tryGetScreenBoundsAndNearestDepth(instanceData, mesh, minUv, maxUv, nearestDepth))
        return false;

    float2 extent = maxUv - minUv;
    if (max(extent.x, extent.y) > 0.35)
        return false;

    float2 center = (minUv + maxUv) * 0.5;
    float2 samples[5] =
    {
        center,
        minUv,
        float2(maxUv.x, minUv.y),
        maxUv,
        float2(minUv.x, maxUv.y)
    };

    uint coveredSamples = 0u;
    const float depthMargin = 0.0025;
    for (uint i = 0u; i < 5u; i++)
    {
        float previousDepth = samplePreviousDepth(samples[i]);
        if (previousDepth >= 0.0 && previousDepth + depthMargin < nearestDepth)
            coveredSamples++;
    }

    return coveredSamples == 5u;
}

[shader("compute")]
[numthreads(GROUP_SIZE, 1, 1)]
void main(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint instanceCount;
    uint instanceStride;
    instances.GetDimensions(instanceCount, instanceStride);
    if (dispatchThreadID.x >= instanceCount)
        return;

    InstanceGpu instanceData = instances[dispatchThreadID.x];
    if (instanceData.MeshId == InvalidInstance)
        return;

    MeshRasterMetadataGpu metadata = rasterMeshes[instanceData.MeshId];
    if (metadata.InstanceCapacity == 0 || metadata.IndexCount == 0)
        return;

    uint slot;
    InterlockedAdd(visibleCounts[instanceData.MeshId], 1u, slot);
    if (slot >= metadata.InstanceCapacity)
        return;

    visibleInstanceIds[metadata.VisibleInstanceOffset + slot] = dispatchThreadID.x;
}
