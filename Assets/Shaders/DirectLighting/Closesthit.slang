#ifndef DIRECTLIGHTING_CLOSESTHIT_SLANG
#define DIRECTLIGHTING_CLOSESTHIT_SLANG

#include "../Shared.slang"

[[vk::push_constant]] ConstantBuffer<PushDataGpu> pushConstants;

#include "..//Geometry/SurfaceData.slang"
#include "..//Materials/PbrMaterial.slang"
#include "..//Integrators/DirectLightingIntegrator.slang"

[shader("closesthit")]
void main(inout Payload payload, in BuiltInTriangleIntersectionAttributes attr)
{
    const uint tlasInstanceIndex = InstanceIndex();
    const uint instanceId = InstanceID();
    const uint primitiveIndex = PrimitiveIndex();

    SurfaceData surface = loadRayTracingSurfaceData(tlasInstanceIndex, primitiveIndex, attr.barycentrics);
    DirectLightingIntegrator<PbrMaterial> integrator;
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

#endif
