// AO RTX ray generation entry point.

#include "../SharedStructs.slang"

[[vk::push_constant]] ConstantBuffer<PushDataGpu> pushConstants;

#include "..//Integrators/AoIntegrator.slang"

[shader("raygeneration")]
void main()
{
    const int2 pixelCoord = int2(DispatchRaysIndex().xy);
    const int2 screenSize = int2(DispatchRaysDimensions().xy);
    AoIntegrator integrator;
    integrator.primaryRayGen(pixelCoord, screenSize);
}