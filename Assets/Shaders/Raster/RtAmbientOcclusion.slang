#include "../Shared.slang"
#include "../Bindings.slang"
#include "../Core/Core.slang"
#include "../Bxdfs/Brdf.slang"
#include "../Geometry/Intersection.slang"

static const int RASTER_RTAO_SAMPLES = 4;
static const float RASTER_RTAO_MAX_DISTANCE = 1.5;
static const float RASTER_RTAO_STRENGTH = 0.65;

float cameraLinearDepth(float3 worldPosition)
{
    float3 cameraForward = normalize(sceneSettings.RasterCamera.Direction);
    return max(dot(worldPosition - sceneSettings.RasterCamera.Position, cameraForward), sceneSettings.RasterCamera.NearPlane);
}

bool loadAoPosition(int2 pixel, int2 screenSize, out float3 position, out float linearDepth)
{
    if (pixel.x < 0 || pixel.y < 0 || pixel.x >= screenSize.x || pixel.y >= screenSize.y)
    {
        position = float3(0.0);
        linearDepth = 0.0;
        return false;
    }

    float4 packedPosition = outputPosition[pixel];
    if (packedPosition.w <= 0.5)
    {
        position = float3(0.0);
        linearDepth = 0.0;
        return false;
    }

    position = packedPosition.xyz;
    linearDepth = cameraLinearDepth(position);
    return true;
}

float3 normalizedDeltaOrZero(float3 fromPosition, float3 toPosition)
{
    float3 delta = toPosition - fromPosition;
    float deltaLength = length(delta);
    return deltaLength > EPSILON ? delta / deltaLength : float3(0.0);
}

float3 computeAoPositionNormal(int2 pixel, int2 screenSize, float3 centerPosition, float3 fallbackNormal, float3 viewVec)
{
    float centerDepth = cameraLinearDepth(centerPosition);

    float3 leftPosition;
    float3 rightPosition;
    float3 topPosition;
    float3 bottomPosition;
    float leftDepth;
    float rightDepth;
    float topDepth;
    float bottomDepth;
    bool hasLeft = loadAoPosition(pixel + int2(-1, 0), screenSize, leftPosition, leftDepth);
    bool hasRight = loadAoPosition(pixel + int2(1, 0), screenSize, rightPosition, rightDepth);
    bool hasTop = loadAoPosition(pixel + int2(0, -1), screenSize, topPosition, topDepth);
    bool hasBottom = loadAoPosition(pixel + int2(0, 1), screenSize, bottomPosition, bottomDepth);

    float4 edgesLRTB = float4(
        hasLeft ? leftDepth - centerDepth : 0.0,
        hasRight ? rightDepth - centerDepth : 0.0,
        hasTop ? topDepth - centerDepth : 0.0,
        hasBottom ? bottomDepth - centerDepth : 0.0);
    edgesLRTB = saturate(1.25 - abs(edgesLRTB) / max(centerDepth * 0.011, 1.0e-3));
    edgesLRTB *= float4(hasLeft ? 1.0 : 0.0, hasRight ? 1.0 : 0.0, hasTop ? 1.0 : 0.0, hasBottom ? 1.0 : 0.0);

    float3 leftDir = hasLeft ? normalizedDeltaOrZero(centerPosition, leftPosition) : float3(0.0);
    float3 rightDir = hasRight ? normalizedDeltaOrZero(centerPosition, rightPosition) : float3(0.0);
    float3 topDir = hasTop ? normalizedDeltaOrZero(centerPosition, topPosition) : float3(0.0);
    float3 bottomDir = hasBottom ? normalizedDeltaOrZero(centerPosition, bottomPosition) : float3(0.0);
    float4 acceptedNormals = saturate(float4(
        edgesLRTB.x * edgesLRTB.z,
        edgesLRTB.z * edgesLRTB.y,
        edgesLRTB.y * edgesLRTB.w,
        edgesLRTB.w * edgesLRTB.x) + 0.01);

    float3 normalSum = acceptedNormals.x * cross(leftDir, topDir)
        + acceptedNormals.y * cross(topDir, rightDir)
        + acceptedNormals.z * cross(rightDir, bottomDir)
        + acceptedNormals.w * cross(bottomDir, leftDir);
    if (length(normalSum) <= EPSILON)
        return fallbackNormal;

    float3 positionNormal = normalize(normalSum);
    if (dot(positionNormal, viewVec) < 0.0)
        positionNormal = -positionNormal;

    return positionNormal;
}

float computeAoRayOriginBias(float3 worldPosition)
{
    return max(computeRayOriginBias(worldPosition) * 4.0, cameraLinearDepth(worldPosition) * 2.5e-4);
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
    int2 screenSize = int2(width, height);
    float4 packedPosition = outputPosition[pixel];
    if (packedPosition.w <= 0.5)
        return;

    float3 normal = normalize(outputNormal[pixel].xyz * 2.0 - 1.0);
    float3 viewVec = normalize(sceneSettings.RasterCamera.Position - packedPosition.xyz);
    float3 originNormal = computeAoPositionNormal(pixel, screenSize, packedPosition.xyz, normal, viewVec);
    normal = stabilizeShadingNormal(normal, originNormal);
    uint2 seed = pcg2d(uint2(dispatchThreadID.xy) ^ uint2(0x9E3779B9u, 0x85EBCA6Bu));
    uint rngState = seed.x ^ seed.y;

    float occluded = 0.0;
    for (int i = 0; i < RASTER_RTAO_SAMPLES; ++i)
    {
        float3 direction = sampleDiffuse(normal, rngState);
        if (dot(direction, originNormal) <= 0.0)
            direction = normalize(direction - originNormal * (2.0 * dot(direction, originNormal)));

        float nDotL = max(dot(originNormal, direction), 0.0);
        float bias = max(computeShadowBias(packedPosition.xyz, nDotL), computeAoRayOriginBias(packedPosition.xyz));
        float3 origin = offsetRayOrigin(packedPosition.xyz, originNormal, direction);
        if (traceShadowRay(origin, direction, bias, RASTER_RTAO_MAX_DISTANCE))
            occluded += 1.0;
    }

    float ao = 1.0 - (occluded / float(RASTER_RTAO_SAMPLES));
    float visibility = lerp(1.0 - RASTER_RTAO_STRENGTH, 1.0, ao);
    float4 color = outputColor[pixel];
    outputColor[pixel] = float4(color.rgb * visibility, color.a);
}
