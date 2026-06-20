#ifndef THINLENS_CAMERA_SLANG
#define THINLENS_CAMERA_SLANG

#include "../Core/Core.slang"
#include "ICamera.slang"

float2 thinLensConcentricSampleDisk(float u1, float u2)
{
    float offsetX = 2.0 * u1 - 1.0;
    float offsetY = 2.0 * u2 - 1.0;
    if (offsetX == 0.0 && offsetY == 0.0)
        return float2(0.0);

    float r;
    float theta;
    if (abs(offsetX) > abs(offsetY)) {
        r = offsetX;
        theta = (PI / 4.0) * (offsetY / offsetX);
    } else {
        r = offsetY;
        theta = (PI / 2.0) - (PI / 4.0) * (offsetX / offsetY);
    }
    return r * float2(cos(theta), sin(theta));
}

float2 thinLensRoundBokeh(float u1, float u2, float edgeBias)
{
    float2 diskSample = thinLensConcentricSampleDisk(u1, u2);
    float r = length(diskSample);
    float newR = pow(r, 1.0 / max(edgeBias, EPSILON));
    if (r > 0.0)
        return diskSample * (newR / r);
    return float2(0.0);
}

struct ThinLensCamera<TSampler> : ICamera<TSampler>
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
        PerspectiveCamera<TSampler> perspectiveCamera;
        perspectiveCamera.generatePerspectiveRay(
            pixelCoord,
            screenSize,
            samplerState,
            sampler,
            deterministicSample,
            rayOrigin,
            rayDirection);

        if (!deterministicSample && camera.Aperture > 0.0) {
            float2 lensSampleRaw = sampler.get2D(samplerState);
            float2 lensSample = thinLensRoundBokeh(lensSampleRaw.x, lensSampleRaw.y, camera.BokehBias) * camera.Aperture;
            // Horizontal and Vertical now store unit basis vectors (scaled by tan(half-FOV))
            // We need to normalize them to get the pure direction for lens sampling
            float3 lensU = normalize(camera.Horizontal);
            float3 lensV = normalize(camera.Vertical);
            float3 rayOriginDOF = camera.Position + lensU * lensSample.x + lensV * lensSample.y;
            float3 focusPoint = rayOrigin + rayDirection * camera.FocusDistance;

            rayOrigin = rayOriginDOF;
            rayDirection = normalize(focusPoint - rayOriginDOF);
        }
    }
};

#endif // THINLENS_CAMERA_SLANG
