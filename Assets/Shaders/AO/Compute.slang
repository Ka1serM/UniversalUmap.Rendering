#include "../SharedStructs.slang"
#include "../Bindings.slang"

[[vk::push_constant]] ConstantBuffer<PushDataGpu> pushConstants;

#include "..//Geometry/Intersection.slang"
#include "..//Geometry/SurfaceData.slang"
#include "..//Integrators/AoIntegrator.slang"

[shader("compute")]
[numthreads(GROUP_SIZE, GROUP_SIZE, 1)]
void main(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    const int2 pixelCoord = int2(dispatchThreadID.xy);
    uint screenWidth;
    uint screenHeight;
    outputColor.GetDimensions(screenWidth, screenHeight);
    const int2 screenSize = int2(screenWidth, screenHeight);
    AoIntegrator integrator;
    integrator.primaryRayGen(pixelCoord, screenSize);
}
