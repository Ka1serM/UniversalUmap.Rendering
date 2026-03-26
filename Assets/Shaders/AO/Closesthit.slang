// AO Closest Hit - RTX entry point

#include "../Shared.slang"

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
        surface.WorldPosition,
        surface.WorldShadowPosition,
        surface.ShadingNormal,
        surface.GeometricNormal,
        surface.Tangent,
        surface.TangentSign,
        surface.UV,
        WorldRayDirection(),
        surface.Material,
        payload);
    payload.ObjectIndex = instanceId;
}
