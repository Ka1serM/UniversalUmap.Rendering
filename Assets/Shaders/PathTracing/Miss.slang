#ifndef PATHTRACING_MISS_SLANG
#define PATHTRACING_MISS_SLANG

#include "../SharedStructs.slang"

[[vk::push_constant]] ConstantBuffer<PushDataGpu> pushConstants;
#include "..//Materials/PbrMaterial.slang"
#include "..//Integrators/PathTracingIntegrator.slang"

[shader("miss")]
void main(inout Payload payload)
{
    PathTracingIntegrator<PbrMaterial> integrator;
    integrator.shadeMiss(WorldRayDirection(), payload);
}

#endif
