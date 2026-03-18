// AO Miss - RTX entry point

#include "../SharedStructs.slang"
#include "..//Integrators/AoIntegrator.slang"

[[vk::push_constant]] ConstantBuffer<PushDataGpu> pushConstants;

[shader("miss")]
void main(inout AoPayload payload)
{
    AoIntegrator integrator;
    integrator.shadeMiss(WorldRayDirection(), payload);
}
