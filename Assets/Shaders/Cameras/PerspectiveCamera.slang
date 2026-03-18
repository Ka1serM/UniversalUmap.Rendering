#ifndef PERSPECTIVE_CAMERA_SLANG
#define PERSPECTIVE_CAMERA_SLANG

#include "ICamera.slang"

struct PerspectiveCamera<TSampler> : ICamera<TSampler>
    where TSampler : ISampler
{
    void generatePerspectiveRay(
        int2 pixelCoord,
        int2 screenSize,
        inout SamplerState samplerState,
        inout TSampler sampler,
        bool deterministicSample,
        out float3 rayOrigin,
        out float3 rayDirection)
    {
        float2 jitter = deterministicSample ? float2(0.0) : (sampler.get2D(samplerState) - 0.5);
        this.generatePerspectiveBaseRay(pixelCoord, screenSize, jitter, rayOrigin, rayDirection);
    }

    void generateRay(
        int2 pixelCoord,
        int2 screenSize,
        inout SamplerState samplerState,
        inout TSampler sampler,
        bool deterministicSample,
        out float3 rayOrigin,
        out float3 rayDirection)
    {
        generatePerspectiveRay(
            pixelCoord,
            screenSize,
            samplerState,
            sampler,
            deterministicSample,
            rayOrigin,
            rayDirection);
    }
};

#endif // PERSPECTIVE_CAMERA_SLANG
