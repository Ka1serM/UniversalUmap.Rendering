#ifndef DIRECTLIGHTING_RAYGEN_SLANG
#define DIRECTLIGHTING_RAYGEN_SLANG

#include "../SharedStructs.slang"

[[vk::push_constant]] ConstantBuffer<PushDataGpu> pushConstants;

#include "..//Materials/PbrMaterial.slang"
#include "..//Integrators/DirectLightingIntegrator.slang"

[shader("raygeneration")]
void main()
{
    const int2 pixelCoord = int2(DispatchRaysIndex().xy);
    const int2 screenSize = int2(DispatchRaysDimensions().xy);
    DirectLightingIntegrator<PbrMaterial> integrator;
    integrator.primaryRayGen(pixelCoord, screenSize);
}

#endif
