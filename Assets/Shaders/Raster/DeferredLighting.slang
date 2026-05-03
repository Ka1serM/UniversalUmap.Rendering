#include "../Shared.slang"
#include "../Bindings.slang"
#include "../Core/Core.slang"
#include "../Bxdfs/Brdf.slang"
#include "../Bxdfs/PbrBxdf.slang"
#include "../Geometry/Intersection.slang"
#include "../Lights/DirectionalLight.slang"
#include "../Lights/EnvironmentLight.slang"

static const float SSIL_WORLD_RADIUS = 2.0;
static const float SSIL_SAMPLE_DISTRIBUTION_POWER = 1.8;
static const float SSIL_AO_POWER = 1.35;
static const float SSIL_HIT_THICKNESS = 0.35;
static const float SSIL_INDIRECT_INTENSITY = 1.0;
static const int SSIL_SLICE_COUNT = 6;
static const int SSIL_STEPS_PER_SLICE = 4;
static const int SSIL_SECTOR_COUNT = 32;
static const float SSIL_PIXEL_TOO_CLOSE_THRESHOLD = 1.3;
static const float SSIL_MAX_SCREEN_RADIUS = 96.0;
static const float SSIL_MIN_SAMPLE_WEIGHT = 1.0 / 32.0;
static const float CONTACT_SHADOW_MAX_DISTANCE = 2.25;
static const int CONTACT_SHADOW_STEPS = 16;
static const int VIRTUAL_SHADOW_BLOCKER_SAMPLE_COUNT = 48;
static const int VIRTUAL_SHADOW_FILTER_SAMPLE_COUNT = 96;
static const float VIRTUAL_SHADOW_BLOCKER_SEARCH_RADIUS_TEXELS = 16.0;
static const float VIRTUAL_SHADOW_LIGHT_RADIUS_TEXELS = 160.0;
static const float VIRTUAL_SHADOW_MIN_FILTER_RADIUS_TEXELS = 0.0;
static const float VIRTUAL_SHADOW_MAX_FILTER_RADIUS_TEXELS = 22.0;
static const float VIRTUAL_SHADOW_RECEIVER_PLANE_BIAS_SCALE = 1.5;
static const float VIRTUAL_SHADOW_VIRTUAL_PAGES_PER_SIDE = 32.0;
static const float VIRTUAL_SHADOW_RESIDENT_PAGES_PER_SIDE = 8.0;
static const int SSR_STEPS = 40;
static const int SSR_REFINE_STEPS = 5;
static const float SSR_MAX_DISTANCE = 40.0;
static const float SSR_MAX_ROUGHNESS = 0.82;
static const float HALF_PI = 1.5707963267948966;

struct DeferredSurface
{
    bool Valid;
    float3 Position;
    float3 Normal;
    float3 Albedo;
    float3 Emission;
    float Metallic;
    float Specular;
    float Roughness;
    float Transmission;
    float Opacity;
};

float3 sampleDiffuseEnvironmentDeferred(float3 normal, EnvironmentDataGpu environmentData)
{
    if (environmentData.IrradianceMapIndex >= 0)
    {
        float2 uv = directionToEnvironmentUv(normal, environmentData);
        return textureSamplers[environmentData.IrradianceMapIndex].Sample(uv).rgb * environmentData.LightingExposureScale;
    }

    return sampleEnvironmentMapColorLod(
        normal,
        environmentData,
        environmentData.LightingExposureScale,
        max(environmentData.MaxTextureLod, 0.0));
}

float3 sampleSpecularEnvironmentDeferred(float3 reflectedDir, float roughness, EnvironmentDataGpu environmentData)
{
    float lod = clamp(roughness, 0.0, 1.0) * max(environmentData.MaxTextureLod, 0.0);
    int specularTextureIndex = environmentData.RadianceMapIndex >= 0
        ? environmentData.RadianceMapIndex
        : environmentData.TextureIndex;
    if (specularTextureIndex < 0)
        return float3(0.0);

    float2 uv = directionToEnvironmentUv(reflectedDir, environmentData);
    return textureSamplers[specularTextureIndex].SampleLevel(uv, max(lod, 0.0)).rgb * environmentData.LightingExposureScale;
}

DeferredSurface loadSurface(int2 pixel)
{
    DeferredSurface surface;
    float4 packedPosition = outputPosition[pixel];
    surface.Valid = packedPosition.w > 0.5;
    surface.Position = packedPosition.xyz;
    surface.Normal = normalize(outputNormal[pixel].xyz * 2.0 - 1.0);
    surface.Albedo = outputAlbedo[pixel].rgb;
    float4 emissionOpacity = outputColor[pixel];
    surface.Emission = emissionOpacity.rgb;
    surface.Opacity = emissionOpacity.a;
    float4 material = outputAdaptiveState[pixel];
    surface.Metallic = saturate(material.x);
    surface.Specular = saturate(material.y);
    surface.Roughness = saturate(max(outputNormal[pixel].w, material.z));
    surface.Transmission = saturate(material.w);
    return surface;
}

float screenEdgeFade(float2 uv, int2 screenSize)
{
    float aspect = float(screenSize.y) / max(float(screenSize.x), 1.0);
    float2 fadeWidth = 0.05 * float2(aspect, 1.0);
    float2 fadeIn = smoothstep(float2(0.0), fadeWidth, uv);
    float2 fadeOut = 1.0 - smoothstep(1.0 - fadeWidth, float2(1.0), uv);
    return saturate(fadeIn.x * fadeIn.y * fadeOut.x * fadeOut.y);
}

bool projectWorldToUv(float3 worldPosition, out float2 uv, out float cameraDistance)
{
    float4 clip = mul(float4(worldPosition, 1.0), sceneSettings.RasterCamera.WorldToClip);
    if (clip.w <= 0.0)
    {
        uv = float2(0.0);
        cameraDistance = 0.0;
        return false;
    }

    float3 ndc = clip.xyz / clip.w;
    if (ndc.x < -1.0 || ndc.x > 1.0 || ndc.y < -1.0 || ndc.y > 1.0 || ndc.z < 0.0 || ndc.z > 1.0)
    {
        uv = float2(0.0);
        cameraDistance = 0.0;
        return false;
    }

    uv = ndc.xy * 0.5 + 0.5;
    float3 cameraForward = normalize(sceneSettings.RasterCamera.Direction);
    cameraDistance = max(dot(worldPosition - sceneSettings.RasterCamera.Position, cameraForward), sceneSettings.RasterCamera.NearPlane);
    return true;
}

float cameraLinearDepth(float3 worldPosition)
{
    float3 cameraForward = normalize(sceneSettings.RasterCamera.Direction);
    return max(dot(worldPosition - sceneSettings.RasterCamera.Position, cameraForward), sceneSettings.RasterCamera.NearPlane);
}

bool loadSsilPosition(int2 pixel, int2 screenSize, out float3 position, out float linearDepth)
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

float3 computeSsilPositionNormal(int2 pixel, int2 screenSize, DeferredSurface surface, float3 viewVec)
{
    float centerDepth = cameraLinearDepth(surface.Position);

    float3 leftPosition;
    float3 rightPosition;
    float3 topPosition;
    float3 bottomPosition;
    float leftDepth;
    float rightDepth;
    float topDepth;
    float bottomDepth;
    bool hasLeft = loadSsilPosition(pixel + int2(-1, 0), screenSize, leftPosition, leftDepth);
    bool hasRight = loadSsilPosition(pixel + int2(1, 0), screenSize, rightPosition, rightDepth);
    bool hasTop = loadSsilPosition(pixel + int2(0, -1), screenSize, topPosition, topDepth);
    bool hasBottom = loadSsilPosition(pixel + int2(0, 1), screenSize, bottomPosition, bottomDepth);

    float4 edgesLRTB = float4(
        hasLeft ? leftDepth - centerDepth : 0.0,
        hasRight ? rightDepth - centerDepth : 0.0,
        hasTop ? topDepth - centerDepth : 0.0,
        hasBottom ? bottomDepth - centerDepth : 0.0);
    float slopeLR = (edgesLRTB.y - edgesLRTB.x) * 0.5;
    float slopeTB = (edgesLRTB.w - edgesLRTB.z) * 0.5;
    float4 slopeAdjusted = edgesLRTB + float4(slopeLR, -slopeLR, slopeTB, -slopeTB);
    edgesLRTB = min(abs(edgesLRTB), abs(slopeAdjusted));
    edgesLRTB = saturate(1.25 - edgesLRTB / max(centerDepth * 0.011, 1.0e-3));
    edgesLRTB *= float4(hasLeft ? 1.0 : 0.0, hasRight ? 1.0 : 0.0, hasTop ? 1.0 : 0.0, hasBottom ? 1.0 : 0.0);

    float3 leftDir = hasLeft ? normalizedDeltaOrZero(surface.Position, leftPosition) : float3(0.0);
    float3 rightDir = hasRight ? normalizedDeltaOrZero(surface.Position, rightPosition) : float3(0.0);
    float3 topDir = hasTop ? normalizedDeltaOrZero(surface.Position, topPosition) : float3(0.0);
    float3 bottomDir = hasBottom ? normalizedDeltaOrZero(surface.Position, bottomPosition) : float3(0.0);
    float4 acceptedNormals = saturate(float4(
        edgesLRTB.x * edgesLRTB.z,
        edgesLRTB.z * edgesLRTB.y,
        edgesLRTB.y * edgesLRTB.w,
        edgesLRTB.w * edgesLRTB.x) + 0.01);
    acceptedNormals *= float4(
        (hasLeft && hasTop) ? 1.0 : 0.0,
        (hasTop && hasRight) ? 1.0 : 0.0,
        (hasRight && hasBottom) ? 1.0 : 0.0,
        (hasBottom && hasLeft) ? 1.0 : 0.0);

    float3 normalSum = acceptedNormals.x * cross(leftDir, topDir)
        + acceptedNormals.y * cross(topDir, rightDir)
        + acceptedNormals.z * cross(rightDir, bottomDir)
        + acceptedNormals.w * cross(bottomDir, leftDir);
    if (length(normalSum) <= EPSILON)
        return surface.Normal;

    float3 positionNormal = normalize(normalSum);
    if (dot(positionNormal, viewVec) < 0.0)
        positionNormal = -positionNormal;

    return positionNormal;
}

float estimateSsilScreenRadius(float centerDepth, int2 screenSize, float effectRadius)
{
    RasterCameraDataGpu camera = sceneSettings.RasterCamera;
    float pixelWorldSizeX = 2.0 * length(camera.Horizontal) * centerDepth / max(float(screenSize.x), 1.0);
    float pixelWorldSizeY = 2.0 * length(camera.Vertical) * centerDepth / max(float(screenSize.y), 1.0);
    float pixelWorldSize = max(min(pixelWorldSizeX, pixelWorldSizeY), 1.0e-4);
    float viewportLimitedRadius = max(24.0, min(SSIL_MAX_SCREEN_RADIUS, min(float(screenSize.x), float(screenSize.y)) * 0.18));
    return clamp(effectRadius / pixelWorldSize, 0.0, viewportLimitedRadius);
}

float computeScreenSpaceRayOriginBias(float3 worldPosition)
{
    return max(computeRayOriginBias(worldPosition) * 4.0, cameraLinearDepth(worldPosition) * 2.5e-4);
}

int countBits32(uint value)
{
    int result = 0;
    for (int bitIndex = 0; bitIndex < 32; ++bitIndex)
        result += int((value >> uint(bitIndex)) & 1u);
    return result;
}

uint buildVisibilitySectorMask(float normalizedAngle0, float normalizedAngle1)
{
    float lo = saturate(min(normalizedAngle0, normalizedAngle1));
    float hi = saturate(max(normalizedAngle0, normalizedAngle1));
    int first = clamp(int(floor(lo * float(SSIL_SECTOR_COUNT))), 0, SSIL_SECTOR_COUNT - 1);
    int last = clamp(int(ceil(hi * float(SSIL_SECTOR_COUNT))), first + 1, SSIL_SECTOR_COUNT);

    uint mask = 0u;
    for (int bitIndex = 0; bitIndex < SSIL_SECTOR_COUNT; ++bitIndex)
    {
        if (bitIndex >= first && bitIndex < last)
            mask |= 1u << uint(bitIndex);
    }

    return mask;
}

struct SsilResult
{
    float Visibility;
    float3 IndirectRadiance;
};

float3 estimateSsilSampleRadiance(DeferredSurface surface)
{
    float3 viewDir = fastNormalize(sceneSettings.RasterCamera.Position - surface.Position);
    float nDotV = max(dot(surface.Normal, viewDir), 0.0);
    float dielectricF0 = dielectricF0FromSpecular(surface.Specular);
    float3 f0 = lerp(float3(dielectricF0), surface.Albedo, surface.Metallic);
    float3 diffuseColor = surface.Albedo * (1.0 - surface.Metallic);
    float3 diffuseEnvironment = sampleDiffuseEnvironmentDeferred(surface.Normal, sceneSettings.Environment);
    float3 reflectedDir = reflect(-viewDir, surface.Normal);
    float3 specularEnvironment = sampleSpecularEnvironmentDeferred(reflectedDir, surface.Roughness, sceneSettings.Environment);
    float3 radiance = surface.Emission
        + diffuseEnvironment * diffuseColor * (1.0 / PI)
        + specularEnvironment * envBrdfApprox(f0, surface.Roughness, nDotV);

    if (hasDirectionalLight(sceneSettings.Environment))
    {
        float3 lightDirection = getDirectionalLightDirection(sceneSettings.Environment);
        float nDotL = max(dot(surface.Normal, lightDirection), 0.0);
        if (nDotL > 0.0)
            radiance += surface.Albedo * float3(sceneSettings.Environment.DirectionalIntensity) * nDotL * (1.0 / PI);
    }

    return max(radiance, float3(0.0));
}

SsilResult computeSsil(int2 pixel, int2 screenSize, DeferredSurface surface)
{
    SsilResult result;
    result.Visibility = 1.0;
    result.IndirectRadiance = float3(0.0);
    if (sceneSettings.RenderSettings.RasterSsilEnabled == 0)
        return result;

    float centerDepth = cameraLinearDepth(surface.Position);
    float effectRadius = min(SSIL_WORLD_RADIUS, max(centerDepth * 0.75, SSIL_WORLD_RADIUS * 0.25));
    float screenRadius = estimateSsilScreenRadius(centerDepth, screenSize, effectRadius);
    if (screenRadius < SSIL_PIXEL_TOO_CLOSE_THRESHOLD)
        return result;

    float3 cameraRight = normalize(sceneSettings.RasterCamera.Horizontal);
    float3 cameraUp = normalize(sceneSettings.RasterCamera.Vertical);
    float3 viewVec = normalize(sceneSettings.RasterCamera.Position - surface.Position);
    float3 ssilNormal = computeSsilPositionNormal(pixel, screenSize, surface, viewVec);
    float3 centerPosition = surface.Position + ssilNormal * computeScreenSpaceRayOriginBias(surface.Position);

    float visibility = 0.0;
    float3 indirectRadiance = float3(0.0);
    float validSliceCount = 0.0;

    for (int sliceIndex = 0; sliceIndex < SSIL_SLICE_COUNT; ++sliceIndex)
    {
        float phi = (float(sliceIndex) + 0.5) * (PI / float(SSIL_SLICE_COUNT));
        float2 sliceDirection = float2(cos(phi), sin(phi));
        float3 directionVec = normalize(cameraRight * sliceDirection.x + cameraUp * sliceDirection.y);
        float3 orthoDirectionVec = directionVec - dot(directionVec, viewVec) * viewVec;
        float orthoLength = length(orthoDirectionVec);
        if (orthoLength <= EPSILON)
            continue;

        orthoDirectionVec /= orthoLength;
        float3 axisVec = normalize(cross(orthoDirectionVec, viewVec));
        float3 projectedNormalVec = ssilNormal - axisVec * dot(ssilNormal, axisVec);
        float projectedNormalLength = length(projectedNormalVec);
        if (projectedNormalLength <= EPSILON)
            continue;

        float signNorm = dot(orthoDirectionVec, projectedNormalVec) >= 0.0 ? 1.0 : -1.0;
        float cosNorm = saturate(dot(projectedNormalVec, viewVec) / projectedNormalLength);
        float n = signNorm * acos(clamp(cosNorm, -1.0, 1.0));

        uint occlusionMask = 0u;
        for (int sideIndex = 0; sideIndex < 2; ++sideIndex)
        {
            float side = sideIndex == 0 ? -1.0 : 1.0;
            for (int stepIndex = 0; stepIndex < SSIL_STEPS_PER_SLICE; ++stepIndex)
            {
                float step01 = (float(stepIndex) + 0.5) / float(SSIL_STEPS_PER_SLICE);
                float sampleDistancePixels = max(
                    SSIL_PIXEL_TOO_CLOSE_THRESHOLD,
                    pow(step01, SSIL_SAMPLE_DISTRIBUTION_POWER) * screenRadius);
                int2 sampleOffset = int2(round(sliceDirection * (sampleDistancePixels * side)));
                if (sampleOffset.x == 0 && sampleOffset.y == 0)
                    continue;

                int2 samplePixel = pixel + sampleOffset;
                float3 samplePosition;
                float sampleDepth;
                if (!loadSsilPosition(samplePixel, screenSize, samplePosition, sampleDepth))
                    continue;

                DeferredSurface sampleSurface = loadSurface(samplePixel);
                float3 sampleDelta = samplePosition - centerPosition;
                float sampleDistance = length(sampleDelta);
                if (sampleDistance <= EPSILON || sampleDistance > effectRadius)
                    continue;

                float planeDistance = dot(samplePosition - surface.Position, ssilNormal);
                float sameSurfaceEpsilon = max(computeScreenSpaceRayOriginBias(surface.Position), sampleDistance * 0.015);
                if (abs(planeDistance) <= sameSurfaceEpsilon && dot(sampleSurface.Normal, ssilNormal) > 0.8)
                    continue;
                if (planeDistance < -max(sameSurfaceEpsilon, sampleDistance * 0.02))
                    continue;

                float3 sampleHorizon = sampleDelta / sampleDistance;
                float frontHorizon = dot(sampleHorizon, viewVec);
                float backHorizon = dot(normalize(sampleDelta - viewVec * SSIL_HIT_THICKNESS), viewVec);
                float2 horizonAngles = acos(clamp(float2(frontHorizon, backHorizon), float2(-1.0), float2(1.0)));
                float2 normalizedAngles = saturate((side * -horizonAngles - n + HALF_PI) * (1.0 / PI));
                normalizedAngles = side >= 0.0 ? normalizedAngles.yx : normalizedAngles;
                uint sectorMask = buildVisibilitySectorMask(normalizedAngles.x, normalizedAngles.y);
                uint newMask = sectorMask & ~occlusionMask;
                float sectorWeight = max(float(countBits32(newMask)) / float(SSIL_SECTOR_COUNT), 0.0);
                if (sectorWeight > 0.0)
                {
                    float3 currentToSample = normalize(samplePosition - surface.Position);
                    float geometryTerm = saturate(dot(ssilNormal, currentToSample)) *
                        saturate(dot(sampleSurface.Normal, -currentToSample));
                    if (geometryTerm > 0.0)
                    {
                        float distanceWeight = saturate(1.0 - sampleDistance / max(effectRadius, EPSILON));
                        distanceWeight = max(distanceWeight * distanceWeight, SSIL_MIN_SAMPLE_WEIGHT);
                        indirectRadiance += estimateSsilSampleRadiance(sampleSurface) * geometryTerm * sectorWeight * distanceWeight;
                    }
                }

                occlusionMask |= sectorMask;
            }
        }

        visibility += 1.0 - float(countBits32(occlusionMask)) / float(SSIL_SECTOR_COUNT);
        validSliceCount += 1.0;
    }

    if (validSliceCount <= EPSILON)
        return result;

    visibility = visibility / validSliceCount;
    visibility = pow(saturate(visibility), SSIL_AO_POWER);
    float smallRadiusFade = saturate((screenRadius - SSIL_PIXEL_TOO_CLOSE_THRESHOLD) / 16.0);
    result.Visibility = lerp(1.0, max(0.03, visibility), smallRadiusFade);
    result.IndirectRadiance = indirectRadiance * (SSIL_INDIRECT_INTENSITY / max(validSliceCount, 1.0));
    return result;
}

float computeScreenSpaceContactShadow(
    int2 pixel,
    DeferredSurface surface,
    int2 screenSize,
    float3 lightDirection,
    float3 originNormal)
{
    if (sceneSettings.RenderSettings.RasterScreenSpaceContactShadowsEnabled == 0)
        return 1.0;

    float nDotL = max(dot(surface.Normal, lightDirection), 0.0);
    if (nDotL <= 0.0)
        return 1.0;

    float bias = max(computeShadowBias(surface.Position, nDotL) * 2.0, computeScreenSpaceRayOriginBias(surface.Position));
    float3 origin = offsetRayOrigin(surface.Position, originNormal, lightDirection) + lightDirection * bias;
    float visibility = 1.0;

    for (int i = 1; i <= CONTACT_SHADOW_STEPS; ++i)
    {
        float step01 = float(i) / float(CONTACT_SHADOW_STEPS);
        float rayDistance = lerp(0.04, CONTACT_SHADOW_MAX_DISTANCE, step01 * step01);
        float3 rayPoint = origin + lightDirection * rayDistance;

        float2 sampleUv;
        float rayCameraDistance;
        if (!projectWorldToUv(rayPoint, sampleUv, rayCameraDistance))
            continue;

        int2 samplePixel = clamp(int2(sampleUv * float2(screenSize)), int2(0), max(screenSize - 1, int2(0)));

        float4 samplePosition = outputPosition[samplePixel];
        if (samplePosition.w <= 0.5 || length(samplePosition.xyz - surface.Position) < max(0.04, bias * 8.0))
            continue;
        if (samplePixel.x == pixel.x && samplePixel.y == pixel.y)
            continue;

        float hitCameraDistance = cameraLinearDepth(samplePosition.xyz);
        float depthDelta = rayCameraDistance - hitCameraDistance;
        float thickness = max(0.04, rayDistance * 0.08);
        if (depthDelta > 0.0 && depthDelta <= thickness)
        {
            float distanceFade = 1.0 - step01;
            float edgeFade = screenEdgeFade(sampleUv, screenSize);
            visibility = min(visibility, lerp(1.0, 0.18, distanceFade * edgeFade));
            break;
        }
    }

    return visibility;
}

float3 estimateDeferredSurfaceRadiance(DeferredSurface surface)
{
    float3 hitViewDir = fastNormalize(sceneSettings.RasterCamera.Position - surface.Position);
    float hitNdotV = max(dot(surface.Normal, hitViewDir), 0.0);
    float dielectricF0 = dielectricF0FromSpecular(surface.Specular);
    float3 hitF0 = lerp(float3(dielectricF0), surface.Albedo, surface.Metallic);
    float3 hitDiffuseColor = surface.Albedo * (1.0 - surface.Metallic);
    float3 hitDiffuseEnvironment = sampleDiffuseEnvironmentDeferred(surface.Normal, sceneSettings.Environment);
    float3 hitReflectedDir = reflect(-hitViewDir, surface.Normal);
    float3 hitSpecularEnvironment = sampleSpecularEnvironmentDeferred(hitReflectedDir, surface.Roughness, sceneSettings.Environment);
    float3 radiance = surface.Emission
        + hitDiffuseEnvironment * hitDiffuseColor * (1.0 / PI)
        + hitSpecularEnvironment * envBrdfApprox(hitF0, surface.Roughness, hitNdotV);

    if (hasDirectionalLight(sceneSettings.Environment))
    {
        float3 lightDirection = getDirectionalLightDirection(sceneSettings.Environment);
        float nDotL = max(dot(surface.Normal, lightDirection), 0.0);
        if (nDotL > 0.0)
        {
            PbrBxdf bxdf;
            BxdfEval bxdfEval = bxdf.evalOpaque(
                hitViewDir,
                surface.Normal,
                surface.Normal,
                lightDirection,
                surface.Albedo,
                surface.Specular,
                surface.Metallic,
                surface.Roughness,
                computeSpecularSamplingWeight(hitViewDir, surface.Normal, surface.Albedo, hitF0, surface.Metallic, surface.Roughness));
            radiance += bxdfEval.f * float3(sceneSettings.Environment.DirectionalIntensity) * nDotL;
        }
    }

    return max(radiance, float3(0.0));
}

bool validateSsrHit(
    DeferredSurface surface,
    int2 screenSize,
    float3 reflectionDir,
    float3 originNormal,
    float3 rayPoint,
    float2 hitUv,
    float rayCameraDistance,
    out float3 reflectedColor,
    out float confidence)
{
    reflectedColor = float3(0.0);
    confidence = 0.0;

    int2 hitPixel = clamp(int2(hitUv * float2(screenSize)), int2(0), max(screenSize - 1, int2(0)));
    DeferredSurface hitSurface = loadSurface(hitPixel);
    if (!hitSurface.Valid || length(hitSurface.Position - surface.Position) < 0.08)
        return false;
    if (abs(dot(hitSurface.Position - surface.Position, originNormal)) < computeScreenSpaceRayOriginBias(surface.Position) &&
        dot(hitSurface.Normal, originNormal) > 0.8)
        return false;

    if (dot(hitSurface.Normal, reflectionDir) > 0.0)
        return false;

    float hitCameraDistance = cameraLinearDepth(hitSurface.Position);
    float depthDistance = abs(hitCameraDistance - rayCameraDistance);
    float thickness = max(0.08, length(rayPoint - surface.Position) * 0.025);
    float hitConfidence = 1.0 - smoothstep(0.0, thickness, depthDistance);
    hitConfidence *= hitConfidence;
    hitConfidence *= screenEdgeFade(hitUv, screenSize);
    if (hitConfidence <= 0.0)
        return false;

    reflectedColor = estimateDeferredSurfaceRadiance(hitSurface);
    confidence = hitConfidence;
    return true;
}

bool refineSsrHit(
    DeferredSurface surface,
    int2 screenSize,
    float3 reflectionDir,
    float3 originNormal,
    float previousDistance,
    float hitDistance,
    out float3 reflectedColor,
    out float confidence)
{
    float minDistance = previousDistance;
    float maxDistance = hitDistance;
    float3 bestColor = float3(0.0);
    float bestConfidence = 0.0;

    for (int i = 0; i < SSR_REFINE_STEPS; ++i)
    {
        float midDistance = (minDistance + maxDistance) * 0.5;
        float3 rayPoint = offsetRayOrigin(surface.Position, originNormal, reflectionDir) + reflectionDir * midDistance;

        float2 hitUv;
        float rayCameraDistance;
        if (!projectWorldToUv(rayPoint, hitUv, rayCameraDistance))
        {
            maxDistance = midDistance;
            continue;
        }

        int2 hitPixel = clamp(int2(hitUv * float2(screenSize)), int2(0), max(screenSize - 1, int2(0)));
        float4 samplePosition = outputPosition[hitPixel];
        if (samplePosition.w <= 0.5)
        {
            minDistance = midDistance;
            continue;
        }

        float hitCameraDistance = cameraLinearDepth(samplePosition.xyz);
        float depthDelta = rayCameraDistance - hitCameraDistance;
        if (depthDelta > 0.0)
            maxDistance = midDistance;
        else
            minDistance = midDistance;

        float3 candidateColor;
        float candidateConfidence;
        if (validateSsrHit(surface, screenSize, reflectionDir, originNormal, rayPoint, hitUv, rayCameraDistance, candidateColor, candidateConfidence)
            && candidateConfidence > bestConfidence)
        {
            bestColor = candidateColor;
            bestConfidence = candidateConfidence;
        }
    }

    reflectedColor = bestColor;
    confidence = bestConfidence;
    return bestConfidence > 0.0;
}

bool traceScreenSpaceReflection(
    int2 pixel,
    DeferredSurface surface,
    int2 screenSize,
    float3 viewDir,
    float3 originNormal,
    out float3 reflectedColor,
    out float confidence)
{
    reflectedColor = float3(0.0);
    confidence = 0.0;
    if (sceneSettings.RenderSettings.RasterScreenSpaceReflectionsEnabled == 0 || surface.Roughness > SSR_MAX_ROUGHNESS)
        return false;

    float3 reflectionDir = normalize(reflect(-viewDir, surface.Normal));
    if (dot(reflectionDir, surface.Normal) <= 0.0)
        return false;

    float3 origin = offsetRayOrigin(surface.Position, originNormal, reflectionDir);
    float previousRayDistance = 0.0;
    float previousDepthDelta = -1.0;
    for (int i = 1; i <= SSR_STEPS; ++i)
    {
        float step01 = float(i) / float(SSR_STEPS);
        float rayDistance = lerp(0.05, SSR_MAX_DISTANCE, step01 * step01);
        float3 rayPoint = origin + reflectionDir * rayDistance;

        float2 hitUv;
        float rayCameraDistance;
        if (!projectWorldToUv(rayPoint, hitUv, rayCameraDistance))
            continue;

        int2 samplePixel = clamp(int2(hitUv * float2(screenSize)), int2(0), max(screenSize - 1, int2(0)));
        float4 samplePosition = outputPosition[samplePixel];
        if (samplePosition.w <= 0.5 ||
            (samplePixel.x == pixel.x && samplePixel.y == pixel.y) ||
            length(samplePosition.xyz - surface.Position) < 0.08)
        {
            previousRayDistance = rayDistance;
            continue;
        }

        float hitCameraDistance = cameraLinearDepth(samplePosition.xyz);
        float thickness = max(0.08, rayDistance * 0.025);
        float depthDelta = rayCameraDistance - hitCameraDistance;
        if ((depthDelta > 0.0 && depthDelta <= thickness) || (previousDepthDelta < 0.0 && depthDelta > 0.0))
        {
            if (refineSsrHit(surface, screenSize, reflectionDir, originNormal, previousRayDistance, rayDistance, reflectedColor, confidence))
                return true;

            if (validateSsrHit(surface, screenSize, reflectionDir, originNormal, rayPoint, hitUv, rayCameraDistance, reflectedColor, confidence))
                return true;
        }

        previousRayDistance = rayDistance;
        previousDepthDelta = depthDelta;
    }

    return false;
}

float computeRtShadowVisibility(DeferredSurface surface, float3 lightDirection, float nDotL, float3 originNormal)
{
    if (sceneSettings.RenderSettings.RasterRtShadowsEnabled == 0)
        return 1.0;

    float shadowBias = max(computeShadowBias(surface.Position, nDotL) * 2.0, computeScreenSpaceRayOriginBias(surface.Position));
    float3 shadowOrigin = offsetRayOrigin(surface.Position, originNormal, lightDirection);
    return traceShadowRay(shadowOrigin, lightDirection, shadowBias, 1000.0) ? 0.0 : 1.0;
}

float4x4 selectShadowWorldToClip(int cascadeIndex)
{
    if (cascadeIndex == 0) return sceneSettings.RasterShadow.WorldToClip0;
    if (cascadeIndex == 1) return sceneSettings.RasterShadow.WorldToClip1;
    if (cascadeIndex == 2) return sceneSettings.RasterShadow.WorldToClip2;
    return sceneSettings.RasterShadow.WorldToClip3;
}

float4 selectShadowUvScaleOffset(int cascadeIndex)
{
    if (cascadeIndex == 0) return sceneSettings.RasterShadow.UvScaleOffset0;
    if (cascadeIndex == 1) return sceneSettings.RasterShadow.UvScaleOffset1;
    if (cascadeIndex == 2) return sceneSettings.RasterShadow.UvScaleOffset2;
    return sceneSettings.RasterShadow.UvScaleOffset3;
}

bool translateVirtualShadowAtlasUvToPhysical(int cascadeIndex, float2 virtualAtlasUv, out float2 physicalAtlasUv)
{
    float4 scaleOffset = selectShadowUvScaleOffset(cascadeIndex);
    float2 virtualPageUv = (virtualAtlasUv - scaleOffset.zw) / max(scaleOffset.xy, float2(EPSILON));
    float2 virtualPageCoord = virtualPageUv * VIRTUAL_SHADOW_VIRTUAL_PAGES_PER_SIDE;
    float residentPageOffset = (VIRTUAL_SHADOW_VIRTUAL_PAGES_PER_SIDE - VIRTUAL_SHADOW_RESIDENT_PAGES_PER_SIDE) * 0.5;
    float2 residentCoord = virtualPageCoord - residentPageOffset;
    if (any(residentCoord < float2(0.0)) || any(residentCoord >= float2(VIRTUAL_SHADOW_RESIDENT_PAGES_PER_SIDE)))
    {
        physicalAtlasUv = float2(0.0);
        return false;
    }

    float2 physicalPageUv = residentCoord / VIRTUAL_SHADOW_RESIDENT_PAGES_PER_SIDE;
    physicalAtlasUv = physicalPageUv * scaleOffset.xy + scaleOffset.zw;
    return true;
}

int selectVirtualShadowCascade(float linearDepth)
{
    float4 farDistances = sceneSettings.RasterShadow.CascadeFarDistances;
    if (linearDepth <= farDistances.x) return 0;
    if (linearDepth <= farDistances.y) return 1;
    if (linearDepth <= farDistances.z) return 2;
    return 3;
}

bool projectToVirtualShadowPage(float3 worldPosition, int cascadeIndex, out float2 atlasUv, out float receiverDepth)
{
    float4 clip = mul(float4(worldPosition, 1.0), selectShadowWorldToClip(cascadeIndex));
    if (clip.w <= 0.0)
    {
        atlasUv = float2(0.0);
        receiverDepth = 0.0;
        return false;
    }

    float3 ndc = clip.xyz / clip.w;
    if (ndc.x < -1.0 || ndc.x > 1.0 || ndc.y < -1.0 || ndc.y > 1.0 || ndc.z < 0.0 || ndc.z > 1.0)
    {
        atlasUv = float2(0.0);
        receiverDepth = 0.0;
        return false;
    }

    float2 pageUv = ndc.xy * 0.5 + 0.5;
    float4 scaleOffset = selectShadowUvScaleOffset(cascadeIndex);
    atlasUv = pageUv * scaleOffset.xy + scaleOffset.zw;
    receiverDepth = saturate(ndc.z);
    return true;
}

void getVirtualShadowPageBounds(
    int cascadeIndex,
    float2 atlasUv,
    out int2 atlasSize,
    out int2 pageMin,
    out int2 pageMax,
    out int2 centerPixel)
{
    uint shadowWidth;
    uint shadowHeight;
    rasterShadowMoments.GetDimensions(shadowWidth, shadowHeight);
    atlasSize = int2(max(shadowWidth, 1u), max(shadowHeight, 1u));

    float4 scaleOffset = selectShadowUvScaleOffset(cascadeIndex);
    pageMin = clamp(int2(floor(scaleOffset.zw * float2(atlasSize))), int2(0), atlasSize - 1);
    pageMax = clamp(int2(ceil((scaleOffset.zw + scaleOffset.xy) * float2(atlasSize))) - 1, pageMin, atlasSize - 1);
    centerPixel = clamp(int2(floor(atlasUv * float2(atlasSize))), pageMin, pageMax);
}

float4 sampleShadowMomentsBilinear(float2 atlasUv, int2 atlasSize, int2 pageMin, int2 pageMax)
{
    float2 texelPosition = atlasUv * float2(atlasSize) - 0.5;
    int2 basePixel = int2(floor(texelPosition));
    float2 blend = frac(texelPosition);

    int2 p00 = clamp(basePixel, pageMin, pageMax);
    int2 p10 = clamp(basePixel + int2(1, 0), pageMin, pageMax);
    int2 p01 = clamp(basePixel + int2(0, 1), pageMin, pageMax);
    int2 p11 = clamp(basePixel + int2(1, 1), pageMin, pageMax);

    float4 row0 = lerp(rasterShadowMoments[p00], rasterShadowMoments[p10], blend.x);
    float4 row1 = lerp(rasterShadowMoments[p01], rasterShadowMoments[p11], blend.x);
    return saturate(lerp(row0, row1, blend.y));
}

float2 virtualShadowSampleRotation(int2 centerPixel)
{
    uint2 seed = pcg2d(uint2(uint(centerPixel.x), uint(centerPixel.y)) ^ uint2(0xA511E9B3u, 0x63D83595u));
    float angle = float(seed.x) * (6.28318530718 / 4294967296.0);
    return float2(cos(angle), sin(angle));
}

float2 rotateOffset(float2 offset, float2 rotationTrig)
{
    return float2(
        offset.x * rotationTrig.x - offset.y * rotationTrig.y,
        offset.y * rotationTrig.x + offset.x * rotationTrig.y);
}

float2 poissonDiskOffset(int sampleIndex, int sampleCount, float2 rotationTrig)
{
    if (sampleIndex == 0)
        return float2(0.0);

    float sampleCountFloat = max(float(sampleCount), 1.0);
    float radius = sqrt((float(sampleIndex) + 0.5) / sampleCountFloat);
    float angle = float(sampleIndex) * 2.39996322973;
    return rotateOffset(float2(cos(angle), sin(angle)) * radius, rotationTrig);
}

bool projectSsilPixelToVirtualShadowPage(
    int2 pixel,
    int2 screenSize,
    int cascadeIndex,
    out float2 atlasUv,
    out float receiverDepth)
{
    if (pixel.x < 0 || pixel.y < 0 || pixel.x >= screenSize.x || pixel.y >= screenSize.y)
    {
        atlasUv = float2(0.0);
        receiverDepth = 0.0;
        return false;
    }

    float4 packedPosition = outputPosition[pixel];
    if (packedPosition.w <= 0.5)
    {
        atlasUv = float2(0.0);
        receiverDepth = 0.0;
        return false;
    }

    return projectToVirtualShadowPage(packedPosition.xyz, cascadeIndex, atlasUv, receiverDepth);
}

bool computeShadowScreenDerivative(
    int2 negativePixel,
    int2 positivePixel,
    int2 screenSize,
    int cascadeIndex,
    float2 centerAtlasUv,
    float centerDepth,
    out float2 atlasTexelDelta,
    out float depthDelta)
{
    float2 negativeUv;
    float2 positiveUv;
    float negativeDepth;
    float positiveDepth;
    bool hasNegative = projectSsilPixelToVirtualShadowPage(
        negativePixel,
        screenSize,
        cascadeIndex,
        negativeUv,
        negativeDepth);
    bool hasPositive = projectSsilPixelToVirtualShadowPage(
        positivePixel,
        screenSize,
        cascadeIndex,
        positiveUv,
        positiveDepth);

    uint shadowWidth;
    uint shadowHeight;
    rasterShadowMoments.GetDimensions(shadowWidth, shadowHeight);
    float virtualToPhysicalScale = VIRTUAL_SHADOW_VIRTUAL_PAGES_PER_SIDE / VIRTUAL_SHADOW_RESIDENT_PAGES_PER_SIDE;
    float2 atlasSize = float2(max(shadowWidth, 1u), max(shadowHeight, 1u)) * virtualToPhysicalScale;

    if (hasNegative && hasPositive)
    {
        atlasTexelDelta = (positiveUv - negativeUv) * atlasSize;
        depthDelta = positiveDepth - negativeDepth;
        return length(atlasTexelDelta) > EPSILON;
    }

    if (hasPositive)
    {
        atlasTexelDelta = (positiveUv - centerAtlasUv) * atlasSize;
        depthDelta = positiveDepth - centerDepth;
        return length(atlasTexelDelta) > EPSILON;
    }

    if (hasNegative)
    {
        atlasTexelDelta = (centerAtlasUv - negativeUv) * atlasSize;
        depthDelta = centerDepth - negativeDepth;
        return length(atlasTexelDelta) > EPSILON;
    }

    atlasTexelDelta = float2(0.0);
    depthDelta = 0.0;
    return false;
}

float2 estimateReceiverDepthGradientPerTexel(
    int2 pixel,
    int2 screenSize,
    int cascadeIndex,
    float2 atlasUv,
    float receiverDepth)
{
    float2 dUvDx;
    float2 dUvDy;
    float dDepthDx;
    float dDepthDy;
    bool hasDx = computeShadowScreenDerivative(
        pixel + int2(-1, 0),
        pixel + int2(1, 0),
        screenSize,
        cascadeIndex,
        atlasUv,
        receiverDepth,
        dUvDx,
        dDepthDx);
    bool hasDy = computeShadowScreenDerivative(
        pixel + int2(0, -1),
        pixel + int2(0, 1),
        screenSize,
        cascadeIndex,
        atlasUv,
        receiverDepth,
        dUvDy,
        dDepthDy);

    if (!hasDx || !hasDy)
        return float2(0.0);

    float determinant = dUvDx.x * dUvDy.y - dUvDx.y * dUvDy.x;
    if (abs(determinant) <= 1.0e-5)
        return float2(0.0);

    float2 gradient;
    gradient.x = (dDepthDx * dUvDy.y - dUvDx.y * dDepthDy) / determinant;
    gradient.y = (dUvDx.x * dDepthDy - dDepthDx * dUvDy.x) / determinant;
    return clamp(gradient, float2(-0.01), float2(0.01));
}

bool findAverageBlockerDepth(
    float2 atlasUv,
    int cascadeIndex,
    float receiverDepth,
    float depthBias,
    float2 receiverDepthGradientPerTexel,
    out float blockerDepth)
{
    int2 atlasSize;
    int2 pageMin;
    int2 pageMax;
    int2 centerPixel;
    getVirtualShadowPageBounds(cascadeIndex, atlasUv, atlasSize, pageMin, pageMax, centerPixel);

    float2 rotationTrig = virtualShadowSampleRotation(centerPixel);
    float blockerDepthSum = 0.0;
    int blockerCount = 0;
    float2 atlasTexelSize = 1.0 / float2(atlasSize);
    for (int i = 0; i < VIRTUAL_SHADOW_BLOCKER_SAMPLE_COUNT; ++i)
    {
        float2 sampleOffsetTexels = poissonDiskOffset(
            i,
            VIRTUAL_SHADOW_BLOCKER_SAMPLE_COUNT,
            rotationTrig) * VIRTUAL_SHADOW_BLOCKER_SEARCH_RADIUS_TEXELS;
        float2 sampleAtlasUv = atlasUv + sampleOffsetTexels * atlasTexelSize;
        float sampleDepth = sampleShadowMomentsBilinear(sampleAtlasUv, atlasSize, pageMin, pageMax).x;
        float blockerThreshold = receiverDepth + dot(sampleOffsetTexels, receiverDepthGradientPerTexel) - depthBias;
        if (sampleDepth < blockerThreshold)
        {
            blockerDepthSum += sampleDepth;
            blockerCount += 1;
        }
    }

    blockerDepth = blockerCount > 0 ? blockerDepthSum / float(blockerCount) : 1.0;
    return blockerCount > 0;
}

float computePcssFilterRadiusTexels(float receiverDepth, float blockerDepth, float depthBias)
{
    float blockerGap = max(receiverDepth - blockerDepth - depthBias, 0.0);
    float penumbra = blockerGap / max(blockerDepth, 0.025);
    float filterRadius = penumbra * VIRTUAL_SHADOW_LIGHT_RADIUS_TEXELS;
    return clamp(
        filterRadius,
        VIRTUAL_SHADOW_MIN_FILTER_RADIUS_TEXELS,
        VIRTUAL_SHADOW_MAX_FILTER_RADIUS_TEXELS);
}

float computeHardShadowIntensity(float2 atlasUv, int cascadeIndex, float receiverDepth, float depthBias)
{
    int2 atlasSize;
    int2 pageMin;
    int2 pageMax;
    int2 centerPixel;
    getVirtualShadowPageBounds(cascadeIndex, atlasUv, atlasSize, pageMin, pageMax, centerPixel);

    float sampleDepth = rasterShadowMoments[centerPixel].x;
    return receiverDepth > sampleDepth + depthBias ? 1.0 : 0.0;
}

float4 sampleFilteredShadowMoments(float2 atlasUv, int cascadeIndex, float filterRadiusTexels)
{
    int2 atlasSize;
    int2 pageMin;
    int2 pageMax;
    int2 centerPixel;
    getVirtualShadowPageBounds(cascadeIndex, atlasUv, atlasSize, pageMin, pageMax, centerPixel);

    float radius = clamp(filterRadiusTexels, 0.0, VIRTUAL_SHADOW_MAX_FILTER_RADIUS_TEXELS);
    if (radius < 0.5)
        return saturate(rasterShadowMoments[centerPixel]);

    float4 moments = float4(0.0);
    float2 rotationTrig = virtualShadowSampleRotation(centerPixel);
    float2 atlasTexelSize = 1.0 / float2(atlasSize);
    for (int i = 0; i < VIRTUAL_SHADOW_FILTER_SAMPLE_COUNT; ++i)
    {
        float2 sampleOffsetTexels = poissonDiskOffset(i, VIRTUAL_SHADOW_FILTER_SAMPLE_COUNT, rotationTrig) * radius;
        float2 sampleAtlasUv = atlasUv + sampleOffsetTexels * atlasTexelSize;
        moments += sampleShadowMomentsBilinear(sampleAtlasUv, atlasSize, pageMin, pageMax);
    }

    return saturate(moments / float(VIRTUAL_SHADOW_FILTER_SAMPLE_COUNT));
}

float computeVarianceShadowIntensity(float4 moments, float fragmentDepth, float depthBias)
{
    if (fragmentDepth <= moments.x + depthBias)
        return 0.0;

    float variance = max(moments.y - moments.x * moments.x, 2.0e-5);
    float depthDelta = fragmentDepth - moments.x;
    float litUpperBound = variance / (variance + depthDelta * depthDelta);
    return 1.0 - saturate(litUpperBound);
}

float computeHamburger4MsmShadowIntensity(float4 moments, float fragmentDepth, float depthBias, float momentBias)
{
    float4 b = lerp(moments, float4(0.5), momentBias);
    float3 z = float3(saturate(fragmentDepth - depthBias), 0.0, 0.0);

    float l32D22 = b.z - b.x * b.y;
    float d22 = b.y - b.x * b.x;
    float squaredDepthVariance = b.w - b.y * b.y;
    float d33D22 = squaredDepthVariance * d22 - l32D22 * l32D22;
    if (d22 <= 1.0e-6 || d33D22 <= 1.0e-10)
        return computeVarianceShadowIntensity(b, fragmentDepth, depthBias);

    float invD22 = 1.0 / d22;
    float l32 = l32D22 * invD22;
    float3 c = float3(1.0, z.x, z.x * z.x);
    c.y -= b.x;
    c.z -= b.y + l32 * c.y;
    c.y *= invD22;
    c.z *= d22 / d33D22;
    c.y -= l32 * c.z;
    c.x -= dot(c.yz, b.xy);
    if (abs(c.z) <= 1.0e-8)
        return computeVarianceShadowIntensity(b, fragmentDepth, depthBias);

    float p = c.y / c.z;
    float q = c.x / c.z;
    float discriminant = p * p * 0.25 - q;
    if (discriminant < 0.0)
        return computeVarianceShadowIntensity(b, fragmentDepth, depthBias);

    float root = sqrt(discriminant);
    z.y = -p * 0.5 - root;
    z.z = -p * 0.5 + root;

    float4 switchData = z.z < z.x
        ? float4(z.y, z.x, 1.0, 1.0)
        : (z.y < z.x ? float4(z.x, z.y, 0.0, 1.0) : float4(0.0));
    float denominator = (z.z - switchData.y) * (z.x - z.y);
    if (abs(denominator) <= 1.0e-8)
        return computeVarianceShadowIntensity(b, fragmentDepth, depthBias);

    float quotient = (switchData.x * z.z - b.x * (switchData.x + z.z) + b.y) / denominator;
    return saturate(switchData.z + switchData.w * quotient);
}

float computeVirtualShadowMapVisibility(
    int2 pixel,
    DeferredSurface surface,
    int2 screenSize,
    float3 lightDirection,
    float nDotL,
    float3 originNormal)
{
    if (sceneSettings.RenderSettings.RasterVirtualShadowMapsEnabled == 0 ||
        sceneSettings.RasterShadow.AtlasSizePageSizeCascadeCountEnabled.w <= 0.5 ||
        nDotL <= 0.0)
    {
        return 1.0;
    }

    float worldBias = max(computeShadowBias(surface.Position, nDotL) * 2.0, computeScreenSpaceRayOriginBias(surface.Position));
    float3 receiverPosition = offsetRayOrigin(surface.Position, originNormal, lightDirection) + lightDirection * worldBias;
    int cascadeIndex = selectVirtualShadowCascade(cameraLinearDepth(surface.Position));

    float2 virtualAtlasUv;
    float2 atlasUv;
    float receiverDepth;
    bool projected = projectToVirtualShadowPage(receiverPosition, cascadeIndex, virtualAtlasUv, receiverDepth) &&
        translateVirtualShadowAtlasUvToPhysical(cascadeIndex, virtualAtlasUv, atlasUv);
    if (!projected)
    {
        for (int fallbackCascade = cascadeIndex + 1; fallbackCascade < 4; ++fallbackCascade)
        {
            if (projectToVirtualShadowPage(receiverPosition, fallbackCascade, virtualAtlasUv, receiverDepth) &&
                translateVirtualShadowAtlasUvToPhysical(fallbackCascade, virtualAtlasUv, atlasUv))
            {
                cascadeIndex = fallbackCascade;
                projected = true;
                break;
            }
        }
    }

    if (!projected)
        return 1.0;

    float normalizedDepthBias = max(0.00035, 0.0015 * (1.0 - nDotL));
    float2 gradientAtlasUv = atlasUv;
    float gradientReceiverDepth = receiverDepth;
    float2 gradientVirtualAtlasUv;
    if (projectToVirtualShadowPage(surface.Position, cascadeIndex, gradientVirtualAtlasUv, gradientReceiverDepth))
        translateVirtualShadowAtlasUvToPhysical(cascadeIndex, gradientVirtualAtlasUv, gradientAtlasUv);
    float2 receiverDepthGradientPerTexel = estimateReceiverDepthGradientPerTexel(
        pixel,
        screenSize,
        cascadeIndex,
        gradientAtlasUv,
        gradientReceiverDepth);
    float blockerDepth;
    bool hasBlocker = findAverageBlockerDepth(
        atlasUv,
        cascadeIndex,
        receiverDepth,
        normalizedDepthBias,
        receiverDepthGradientPerTexel,
        blockerDepth);
    if (!hasBlocker)
        return 1.0;

    float filterRadiusTexels = computePcssFilterRadiusTexels(receiverDepth, blockerDepth, normalizedDepthBias);
    float receiverPlaneBias = dot(abs(receiverDepthGradientPerTexel), float2(filterRadiusTexels)) *
        VIRTUAL_SHADOW_RECEIVER_PLANE_BIAS_SCALE;
    float comparisonDepthBias = normalizedDepthBias + receiverPlaneBias;
    float shadowIntensity = filterRadiusTexels < 0.5
        ? computeHardShadowIntensity(atlasUv, cascadeIndex, receiverDepth, comparisonDepthBias)
        : computeHamburger4MsmShadowIntensity(
            sampleFilteredShadowMoments(atlasUv, cascadeIndex, filterRadiusTexels),
            receiverDepth,
            comparisonDepthBias,
            3.0e-5);

    float4 scaleOffset = selectShadowUvScaleOffset(cascadeIndex);
    float2 pageUv = saturate((atlasUv - scaleOffset.zw) / max(scaleOffset.xy, float2(EPSILON)));
    float edgeDistance = min(min(pageUv.x, pageUv.y), min(1.0 - pageUv.x, 1.0 - pageUv.y));
    float edgeFade = smoothstep(0.0, 0.025, edgeDistance);
    return lerp(1.0, 1.0 - shadowIntensity, edgeFade);
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
    DeferredSurface surface = loadSurface(pixel);
    if (!surface.Valid)
        return;

    float3 viewDir = fastNormalize(sceneSettings.RasterCamera.Position - surface.Position);
    float3 originNormal = computeSsilPositionNormal(pixel, screenSize, surface, viewDir);
    float nDotV = max(dot(surface.Normal, viewDir), 0.0);
    float dielectricF0 = dielectricF0FromSpecular(surface.Specular);
    float3 f0 = lerp(float3(dielectricF0), surface.Albedo, surface.Metallic);
    float3 diffuseColor = surface.Albedo * (1.0 - surface.Metallic);

    SsilResult ssil = computeSsil(pixel, screenSize, surface);
    float3 diffuseEnvironment = sampleDiffuseEnvironmentDeferred(surface.Normal, sceneSettings.Environment);
    float3 reflectedDir = reflect(-viewDir, surface.Normal);
    float3 specularEnvironment = sampleSpecularEnvironmentDeferred(reflectedDir, surface.Roughness, sceneSettings.Environment);

    float3 diffuseAmbient = diffuseEnvironment * diffuseColor * (1.0 / PI) * ssil.Visibility;
    float3 screenSpaceIndirect = diffuseColor * ssil.IndirectRadiance * (1.0 / PI);
    float specularAo = computeSpecularOcclusion(nDotV, ssil.Visibility, surface.Roughness);
    float3 specularAmbient = specularEnvironment * envBrdfApprox(f0, surface.Roughness, nDotV) * specularAo;

    float3 ssrColor;
    float ssrConfidence;
    if (traceScreenSpaceReflection(pixel, surface, screenSize, viewDir, originNormal, ssrColor, ssrConfidence))
    {
        float ssrWeight = ssrConfidence * saturate(1.0 - surface.Roughness / SSR_MAX_ROUGHNESS);
        specularAmbient = lerp(specularAmbient, ssrColor * envBrdfApprox(f0, surface.Roughness, nDotV), ssrWeight);
    }

    float3 directSun = float3(0.0);
    if (hasDirectionalLight(sceneSettings.Environment))
    {
        float3 lightDirection = getDirectionalLightDirection(sceneSettings.Environment);
        float nDotL = max(dot(surface.Normal, lightDirection), 0.0);
        if (nDotL > 0.0)
        {
            PbrBxdf bxdf;
            BxdfEval bxdfEval = bxdf.evalOpaque(
                viewDir,
                surface.Normal,
                surface.Normal,
                lightDirection,
                surface.Albedo,
                surface.Specular,
                surface.Metallic,
                surface.Roughness,
                computeSpecularSamplingWeight(viewDir, surface.Normal, surface.Albedo, f0, surface.Metallic, surface.Roughness));

            float visibility = computeVirtualShadowMapVisibility(pixel, surface, screenSize, lightDirection, nDotL, originNormal);
            visibility *= computeScreenSpaceContactShadow(pixel, surface, screenSize, lightDirection, originNormal);
            visibility *= computeRtShadowVisibility(surface, lightDirection, nDotL, originNormal);
            directSun = bxdfEval.f * float3(sceneSettings.Environment.DirectionalIntensity) * nDotL * visibility;
        }
    }

    float3 finalColor = max(surface.Emission + diffuseAmbient + screenSpaceIndirect + specularAmbient + directSun, float3(0.0));
    outputColor[pixel] = float4(finalColor, surface.Opacity);
}
