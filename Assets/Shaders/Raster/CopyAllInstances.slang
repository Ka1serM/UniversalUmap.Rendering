#include "../Shared.slang"
#include "../Bindings.slang"

[shader("compute")]
[numthreads(GROUP_SIZE, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint count, stride;
    instances.GetDimensions(count, stride);
    if (id.x >= count) return;

    InstanceGpu inst = instances[id.x];
    if (inst.MeshId == InvalidInstance) return;

    MeshRasterMetadataGpu meta = rasterMeshes[inst.MeshId];
    if (meta.InstanceCapacity == 0) return;

    uint slot;
    InterlockedAdd(visibleCounts[inst.MeshId], 1u, slot);
    if (slot >= meta.InstanceCapacity) return;

    visibleInstanceIds[meta.VisibleInstanceOffset + slot] = id.x;
}
