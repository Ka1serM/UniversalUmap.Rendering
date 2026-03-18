#ifndef PATHTRACING_PIPELINE_SLANG
#define PATHTRACING_PIPELINE_SLANG

#include "../SharedStructs.slang"
#include "../Bindings.slang"
#include "..//Geometry/Intersection.slang"

[[vk::push_constant]] ConstantBuffer<PushDataGpu> pushConstants;

#include "..//Materials/PbrMaterial.slang"
#include "..//Integrators/PathTracingIntegrator.slang"

[shader("compute")]
[numthreads(GROUP_SIZE, GROUP_SIZE, 1)]
void main(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    const int2 pixelCoord = int2(dispatchThreadID.xy);
    uint screenWidth;
    uint screenHeight;
    outputColor.GetDimensions(screenWidth, screenHeight);
    const int2 screenSize = int2(screenWidth, screenHeight);
    PathTracingIntegrator<PbrMaterial> integrator;
    integrator.primaryRayGen(pixelCoord, screenSize);
}

#endif
