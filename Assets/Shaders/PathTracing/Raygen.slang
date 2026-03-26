#ifndef PATHTRACING_RAYGEN_SLANG
#define PATHTRACING_RAYGEN_SLANG

#include "../Shared.slang"

[[vk::push_constant]] ConstantBuffer<PushDataGpu> pushConstants;

#include "..//Materials/PbrMaterial.slang"
#include "..//Integrators/PathTracingIntegrator.slang"

[shader("raygeneration")]
void main()
{
    const int2 pixelCoord = int2(DispatchRaysIndex().xy);
    const int2 screenSize = int2(DispatchRaysDimensions().xy);
    PathTracingIntegrator<PbrMaterial> integrator;
    integrator.primaryRayGen(pixelCoord, screenSize);
}

#endif
