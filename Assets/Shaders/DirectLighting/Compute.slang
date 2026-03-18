#ifndef DIRECTLIGHTING_PIPELINE_SLANG
#define DIRECTLIGHTING_PIPELINE_SLANG

#include "../SharedStructs.slang"

[[vk::push_constant]] ConstantBuffer<PushDataGpu> pushConstants;

#include "..//Geometry/Intersection.slang"
#include "..//Geometry/SurfaceData.slang"
#include "..//Materials/PbrMaterial.slang"
#include "..//Integrators/DirectLightingIntegrator.slang"

[shader("compute")]
[numthreads(GROUP_SIZE, GROUP_SIZE, 1)]
void main(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    const int2 pixelCoord = int2(dispatchThreadID.xy);
    uint screenWidth;
    uint screenHeight;
    outputColor.GetDimensions(screenWidth, screenHeight);
    const int2 screenSize = int2(screenWidth, screenHeight);
    DirectLightingIntegrator<PbrMaterial> integrator;
    integrator.primaryRayGen(pixelCoord, screenSize);
}

#endif
