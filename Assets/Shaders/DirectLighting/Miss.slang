#ifndef DIRECTLIGHTING_MISS_SLANG
#define DIRECTLIGHTING_MISS_SLANG

#include "../SharedStructs.slang"

[[vk::push_constant]] ConstantBuffer<PushDataGpu> pushConstants;

#include "..//Materials/PbrMaterial.slang"
#include "..//Integrators/DirectLightingIntegrator.slang"

[shader("miss")]
void main(inout Payload payload)
{
    DirectLightingIntegrator<PbrMaterial> integrator;
    integrator.shadeMiss(WorldRayDirection(), payload);
}

#endif
