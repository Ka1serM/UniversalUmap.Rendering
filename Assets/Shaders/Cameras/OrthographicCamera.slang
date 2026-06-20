#ifndef ORTHOGRAPHIC_CAMERA_SLANG
#define ORTHOGRAPHIC_CAMERA_SLANG

#include "ICamera.slang"

struct OrthographicCamera<TSampler> : ICamera<TSampler>
    where TSampler : ISampler
{
    void generateRay(
        int2 pixelCoord,
        int2 screenSize,
        inout SamplerState samplerState,
        inout TSampler sampler,
        bool deterministicSample,
        out float3 rayOrigin,
        out float3 rayDirection)
    {
        CameraDataGpu camera = pushConstants.Camera;
        float2 uv = deterministicSample
            ? (float2(pixelCoord) + 0.5) / float2(screenSize)
            : (float2(pixelCoord) + sampler.get2D(samplerState)) / float2(screenSize);
        float2 sensorOffset = uv - 0.5;

        // Horizontal and Vertical store half-extents of the orthographic view volume.
        // sensorOffset maps [-0.5, 0.5] across the image, so the ray origin
        // sweeps across the full view rectangle.
        rayOrigin = camera.Position
            + camera.Horizontal * sensorOffset.x
            + camera.Vertical * sensorOffset.y;
        rayDirection = normalize(camera.Direction);
    }
};

#endif // ORTHOGRAPHIC_CAMERA_SLANG
