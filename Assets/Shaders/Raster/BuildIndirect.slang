#include "../Shared.slang"
#include "../Bindings.slang"

[[vk::binding(10, 0)]] StructuredBuffer<MeshRasterMetadataGpu, Std430DataLayout> rasterMeshes;
[[vk::binding(12, 0)]] RWStructuredBuffer<uint> visibleCounts;
[[vk::binding(13, 0)]] RWStructuredBuffer<DrawIndirectCommandGpu> indirectCommands;

[shader("compute")]
[numthreads(GROUP_SIZE, 1, 1)]
void main(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint meshCount;
    uint meshStride;
    rasterMeshes.GetDimensions(meshCount, meshStride);
    if (dispatchThreadID.x >= meshCount)
        return;

    MeshRasterMetadataGpu metadata = rasterMeshes[dispatchThreadID.x];
    indirectCommands[dispatchThreadID.x] = DrawIndirectCommandGpu(
        metadata.IndexCount,
        visibleCounts[dispatchThreadID.x],
        0u,
        0u);
}
