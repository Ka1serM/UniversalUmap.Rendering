#ifndef FISHEYE_CAMERA_SLANG
#define FISHEYE_CAMERA_SLANG

#include "../Core/Core.slang"
#include "ICamera.slang"
#include "ThinLensCamera.slang"

struct FisheyeCamera<TSampler> : ICamera<TSampler>
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

        float aspect = float(screenSize.x) / float(screenSize.y);
        float2 uvCentered = float2(sensorOffset.x * aspect, sensorOffset.y) * 2.0;
        float r = length(uvCentered);
        float maxR = length(float2(aspect, 1.0));

        float maxTheta = camera.FisheyeFov * 0.5;
        float theta = (r / maxR) * maxTheta;
        theta = min(theta, PI * 0.5);

        float3 forward = normalize(camera.Direction);
        float3 right = normalize(camera.Horizontal);
        float3 up = normalize(camera.Vertical);

        rayOrigin = camera.Position;

        if (r > 1e-6) {
            float phi = atan2(uvCentered.y, uvCentered.x);
            rayDirection = normalize(
                sin(theta) * cos(phi) * right +
                sin(theta) * sin(phi) * up +
                cos(theta) * forward);
        } else {
            rayDirection = forward;
        }

        if (!deterministicSample && !isInteractiveFrame() && camera.Aperture > 0.0) {
            float2 lensSampleRaw = sampler.get2D(samplerState);
            float2 lensSample = thinLensRoundBokeh(lensSampleRaw.x, lensSampleRaw.y, camera.BokehBias) * camera.Aperture;
            float3 rayOriginDOF = camera.Position + right * lensSample.x + up * lensSample.y;
            float3 focusPoint = rayOrigin + rayDirection * camera.FocusDistance;

            rayOrigin = rayOriginDOF;
            rayDirection = normalize(focusPoint - rayOriginDOF);
        }
    }
};

#endif // FISHEYE_CAMERA_SLANG
