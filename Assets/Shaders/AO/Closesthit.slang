// AO Closest Hit - RTX entry point

#include "../SharedStructs.slang"

[[vk::push_constant]] ConstantBuffer<PushDataGpu> pushConstants;

#include "..//Geometry/SurfaceData.slang"
#include "..//Integrators/AoIntegrator.slang"

[shader("closesthit")]
void main(inout AoPayload payload, in BuiltInTriangleIntersectionAttributes attr)
{
    const uint tlasInstanceIndex = InstanceIndex();
    const uint instanceId = InstanceID();
    const uint primitiveIndex = PrimitiveIndex();

    SurfaceData surface = loadRayTracingSurfaceData(tlasInstanceIndex, primitiveIndex, attr.barycentrics);
    AoIntegrator integrator;
    integrator.shadeClosestHit(
        surface.worldPosition,
        surface.worldShadowPosition,
        surface.shadingNormal,
        surface.geometricNormal,
        surface.tangent,
        surface.tangentSign,
        surface.uv,
        WorldRayDirection(),
        surface.material,
        payload);
    payload.objectIndex = instanceId;
}
