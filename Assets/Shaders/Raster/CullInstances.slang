#include "../Shared.slang"
#include "../Bindings.slang"

float4 projectCorner(float3 localMin, float3 localMax, uint corner, float4x4 transform)
{
    float3 localPos = float3(
        (corner & 1u) != 0u ? localMax.x : localMin.x,
        (corner & 2u) != 0u ? localMax.y : localMin.y,
        (corner & 4u) != 0u ? localMax.z : localMin.z);
    float3 worldPos = mul(float4(localPos, 1.0), transform).xyz;
    return mul(float4(worldPos, 1.0), sceneSettings.RasterCamera.WorldToClip);
}

bool isOutsideFrustum(InstanceGpu inst, MeshAddressesGpu mesh)
{
    int outL = 0, outR = 0, outB = 0, outT = 0, outN = 0, outF = 0;
    [unroll] for (uint i = 0; i < 8; i++)
    {
        float4 c = projectCorner(mesh.LocalBoundsMin, mesh.LocalBoundsMax, i, inst.Transform);
        outL += c.x < -c.w ? 1 : 0;
        outR += c.x > c.w ? 1 : 0;
        outB += c.y < -c.w ? 1 : 0;
        outT += c.y > c.w ? 1 : 0;
        outN += c.z < 0.0 ? 1 : 0;
        outF += c.z > c.w ? 1 : 0;
    }
    return outL == 8 || outR == 8 || outB == 8 || outT == 8 || outN == 8 || outF == 8;
}

[shader("compute")]
[numthreads(GROUP_SIZE, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint count, stride;
    instances.GetDimensions(count, stride);
    if (id.x >= count) return;

    InstanceGpu inst = instances[id.x];
    if (inst.MeshId == InvalidInstance) return;

    MeshAddressesGpu mesh = meshes[inst.MeshId];
    MeshRasterMetadataGpu meta = rasterMeshes[inst.MeshId];
    if (meta.InstanceCapacity == 0 || meta.IndexCount == 0) return;

    if (isOutsideFrustum(inst, mesh)) return;

    uint slot;
    InterlockedAdd(visibleCounts[inst.MeshId], 1u, slot);
    if (slot >= meta.InstanceCapacity) return;

    visibleInstanceIds[meta.VisibleInstanceOffset + slot] = id.x;
}
