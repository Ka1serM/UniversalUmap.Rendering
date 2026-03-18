#ifndef ICAMERA_SLANG
#define ICAMERA_SLANG

#include "../SharedStructs.slang"
#include "../Bindings.slang"
#include "../Samplers/ISampler.slang"

interface ICamera<TSampler>
    where TSampler : ISampler
{
    void generatePerspectiveBaseRay(
        int2 pixelCoord,
        int2 screenSize,
        float2 jitter,
        out float3 rayOrigin,
        out float3 rayDirection)
    {
        CameraDataGpu camera = sceneSettings.camera;
        float2 uv = (float2(pixelCoord) + jitter) / float2(screenSize);
        float2 sensorOffset = uv - 0.5;

        float3 camPos = camera.position;
        float3 camDir = normalize(camera.direction);
        float3 horizontal = camera.horizontal;
        float3 vertical = camera.vertical;

        float3 imagePlaneCenter = camPos + camDir * camera.focalLength;
        float3 imagePlanePoint = imagePlaneCenter + horizontal * sensorOffset.x + vertical * sensorOffset.y;

        rayOrigin = camPos;
        rayDirection = normalize(imagePlanePoint - rayOrigin);
    }

    void generateRay(
        int2 pixelCoord,
        int2 screenSize,
        inout SamplerState samplerState,
        inout TSampler sampler,
        bool deterministicSample,
        out float3 rayOrigin,
        out float3 rayDirection);
};

#endif // ICAMERA_SLANG
