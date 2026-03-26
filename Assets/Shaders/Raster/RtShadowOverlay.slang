#include "../Shared.slang"
#include "../Bindings.slang"
#include "../Core/Core.slang"
#include "../Geometry/Intersection.slang"
#include "../Lights/DirectionalLight.slang"

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
    float4 packedPosition = outputPosition[pixel];
    if (packedPosition.w <= 0.5 || !hasDirectionalLight(sceneSettings.Environment))
        return;

    float3 normal = outputNormal[pixel].xyz * 2.0 - 1.0;
    float3 lightDirection = getDirectionalLightDirection(sceneSettings.Environment);
    float nDotL = max(dot(normalize(normal), lightDirection), 0.0);
    if (nDotL <= 0.0)
        return;

    float shadowBias = computeShadowBias(packedPosition.xyz, nDotL) * 2.0;
    float3 shadowOrigin = packedPosition.xyz + normalize(normal) * shadowBias;
    if (!traceShadowRay(shadowOrigin, lightDirection, shadowBias, 1000.0))
        return;

    float3 shadowedColor = max(outputColor[pixel].rgb - outputAdaptiveState[pixel].rgb, 0.0);
    outputColor[pixel] = float4(shadowedColor, outputColor[pixel].a);
}
