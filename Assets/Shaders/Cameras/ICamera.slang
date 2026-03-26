#ifndef ICAMERA_SLANG
#define ICAMERA_SLANG

#include "../Shared.slang"
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
        CameraDataGpu camera = pushConstants.Camera;
        float2 uv = (float2(pixelCoord) + jitter) / float2(screenSize);
        // Sensor offset ranges from -0.5 to 0.5, mapping pixel coordinates to NDC
        float2 sensorOffset = uv - 0.5;

        float3 camPos = camera.Position;
        float3 camDir = normalize(camera.Direction);
        // Horizontal and Vertical now store unit basis vectors scaled by tan(half-FOV)
        // We multiply by focal length to get the image plane offset
        float3 horizontal = camera.Horizontal;
        float3 vertical = camera.Vertical;

        // Image plane is at focal length distance
        // Offset from center is: basis * tan(half-FOV) * focalLength * sensorOffset
        // Since Horizontal/Vertical already contain tan(half-FOV), we multiply by focalLength
        float3 imagePlanePoint = camPos
            + camDir * camera.FocalLength 
            + horizontal * camera.FocalLength * sensorOffset.x 
            + vertical * camera.FocalLength * sensorOffset.y;

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
