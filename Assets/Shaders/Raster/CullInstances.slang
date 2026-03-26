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
