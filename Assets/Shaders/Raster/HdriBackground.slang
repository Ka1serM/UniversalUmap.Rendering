#include "../Shared.slang"
#include "../Bindings.slang"
#include "../Core/Core.slang"
#include "../Lights/EnvironmentLight.slang"

float3 computeRasterCameraRayDirection(int2 pixel, int2 screenSize)
{
    float2 uv = (float2(pixel) + 0.5) / float2(screenSize);
    float2 sensorOffset = uv - 0.5;
    RasterCameraDataGpu camera = sceneSettings.RasterCamera;
    float3 imagePlanePoint = camera.Position
        + normalize(camera.Direction) * camera.FocalLength
        + camera.Horizontal * camera.FocalLength * sensorOffset.x
        + camera.Vertical * camera.FocalLength * sensorOffset.y;
    return normalize(imagePlanePoint - camera.Position);
}

[shader("compute")]
[numthreads(GROUP_SIZE, GROUP_SIZE, 1)]
void main(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint width;
    uint height;
    outputColor.GetDimensions(width, height);
    if (dispatchThreadID.x >= width || dispatchThreadID.y >= height)
        return;

    int2 pixel = int2(dispatchThreadID.xy);
    if (outputPosition[pixel].w > 0.5)
        return;

    if (sceneSettings.Environment.Visible == 0 || sceneSettings.RenderSettings.TransparentBackground != 0)
    {
        outputColor[pixel] = float4(0.0);
        return;
    }

    float3 rayDirection = computeRasterCameraRayDirection(pixel, int2(width, height));
    float3 envColor = evaluateEnvironmentVisibleRadiance(rayDirection, sceneSettings.Environment);
    outputColor[pixel] = float4(envColor, 1.0);
}
