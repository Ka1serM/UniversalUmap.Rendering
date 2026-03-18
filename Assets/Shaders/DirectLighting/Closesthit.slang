#ifndef DIRECTLIGHTING_CLOSESTHIT_SLANG
#define DIRECTLIGHTING_CLOSESTHIT_SLANG

#include "../SharedStructs.slang"

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

#endif
