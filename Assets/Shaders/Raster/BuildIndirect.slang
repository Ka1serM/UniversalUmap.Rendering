#include "../Shared.slang"
#include "../Bindings.slang"

[shader("compute")]
[numthreads(GROUP_SIZE, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint meshCount, stride;
    rasterMeshes.GetDimensions(meshCount, stride);
    if (id.x >= meshCount) return;

    MeshRasterMetadataGpu meta = rasterMeshes[id.x];
    indirectCommands[id.x] = DrawIndirectCommandGpu(
        meta.IndexCount,
        visibleCounts[id.x],
        0u,
        meta.VisibleInstanceOffset);

    // Reset visibility count for next frame
    visibleCounts[id.x] = 0u;
}
