#ifndef ENVIRONMENT_LIGHT_SLANG
#define ENVIRONMENT_LIGHT_SLANG

#include "../Bindings.slang"
#include "../Core/Core.slang"
#include "../Bxdfs/Brdf.slang"
#include "../Core/Payload.slang"
#include "ILight.slang"

float2 directionToEnvironmentUv(in float3 worldRayDirection, in EnvironmentDataGpu environmentData) {
    float3 rotatedDir = worldRayDirection;
    rotatedDir.x = worldRayDirection.x * environmentData.rotationCos - worldRayDirection.z * environmentData.rotationSin;
    rotatedDir.z = worldRayDirection.x * environmentData.rotationSin + worldRayDirection.z * environmentData.rotationCos;

    float2 uv;
    uv.x = atan2(rotatedDir.z, rotatedDir.x) / (2.0 * PI) + 0.5;
    uv.y = 1.0 - acos(clamp(rotatedDir.y, -1.0, 1.0)) / PI;
    return uv;
}

float3 sampleEnvironmentMapColor(in float3 worldRayDirection, in EnvironmentDataGpu environmentData, in float exposureStops) {
    if (environmentData.textureIndex == -1)
        return float3(1.0);

    float2 uv = directionToEnvironmentUv(worldRayDirection, environmentData);
    return textureSamplers[environmentData.textureIndex].Sample(uv).rgb * exposureStops;
}

float3 sampleEnvironmentMapColorLod(in float3 worldRayDirection, in EnvironmentDataGpu environmentData, in float exposureStops, in float lod) {
    if (environmentData.textureIndex == -1)
        return float3(1.0);

    float2 uv = directionToEnvironmentUv(worldRayDirection, environmentData);
    return textureSamplers[environmentData.textureIndex].SampleLevel(uv, max(lod, 0.0)).rgb * exposureStops;
}

float evaluateEnvironmentPdfForDirection(in float3 worldRayDirection, in EnvironmentDataGpu environmentData) {
    if (environmentData.textureIndex < 0 || environmentData.cdfTextureIndex < 0)
        return 0.0;

    uint envWidth;
    uint envHeight;
    textureSamplers[environmentData.textureIndex].GetDimensions(envWidth, envHeight);
    int2 envSize = int2(envWidth, envHeight);
    if (envSize.x <= 0 || envSize.y <= 0)
        return 0.0;

    float2 uv = directionToEnvironmentUv(worldRayDirection, environmentData);
    int x = clamp(int(floor(uv.x * float(envSize.x))), 0, envSize.x - 1);
    int y = clamp(int(floor(uv.y * float(envSize.y))), 0, envSize.y - 1);
    float uvPdf = max(textureSamplers[environmentData.cdfTextureIndex].Load(int3(int2(x, y), 0)).a, 0.0);
    float theta = uv.y * PI;
    float sinTheta = max(sin(theta), EPSILON);
    return uvPdf / max(2.0 * PI * PI * sinTheta, EPSILON);
}

float evaluateNeeLightPdfForDirection(in float3 worldRayDirection, in EnvironmentDataGpu environmentData) {
    return evaluateEnvironmentPdfForDirection(worldRayDirection, environmentData);
}

float3 evaluateEnvironmentRadiance(in float3 worldRayDirection, in EnvironmentDataGpu environmentData) {
    if (environmentData.textureIndex == -1)
        return float3(1.0);

    float2 uv = directionToEnvironmentUv(worldRayDirection, environmentData);
    return textureSamplers[environmentData.textureIndex].Sample(uv).rgb * environmentData.lightingExposureScale;
}

float3 evaluateEnvironmentVisibleRadiance(in float3 worldRayDirection, in EnvironmentDataGpu environmentData) {
    if (environmentData.textureIndex == -1)
        return float3(1.0);

    float2 uv = directionToEnvironmentUv(worldRayDirection, environmentData);
    return textureSamplers[environmentData.textureIndex].Sample(uv).rgb * environmentData.visibleExposureScale;
}

void shadeMiss(in float3 worldRayDirection, in EnvironmentDataGpu environmentData, in RenderSettingsDataGpu renderSettings, inout Payload payload) {
    float3 envColor = payload.depth == 0
        ? evaluateEnvironmentVisibleRadiance(worldRayDirection, environmentData)
        : evaluateEnvironmentRadiance(worldRayDirection, environmentData);

    if (payload.depth > 0 && payload.lastBsdfPdf > 0.0) {
        float neeLightPdf = evaluateNeeLightPdfForDirection(worldRayDirection, environmentData);
        float wBsdf = powerHeuristic(payload.lastBsdfPdf, neeLightPdf);
        envColor *= wBsdf;
    }

    payload.attenuation = float3(1.0);
    payload.emission    = envColor;
    payload.albedo      = envColor;
    payload.normal      = float3(0.0);

    if ((payload.depth == 0 && environmentData.visible == 0) || (payload.depth == 0 && renderSettings.transparentBackground != 0))
        payload.flags |= uint(BounceType.ENV_TRANSPARENT);

    payload.flags |= uint(BounceType.RAY_TERMINATED);
}

void shadeMiss(in float3 worldRayDirection, in EnvironmentDataGpu environmentData, in RenderSettingsDataGpu renderSettings, inout AoPayload payload) {
    payload.emission = evaluateEnvironmentVisibleRadiance(worldRayDirection, environmentData);
    payload.albedo = payload.emission;
    payload.normal = float3(0.0);

    if (environmentData.visible == 0 || renderSettings.transparentBackground != 0)
        payload.flags |= uint(BounceType.ENV_TRANSPARENT);

    payload.flags |= uint(BounceType.RAY_TERMINATED);
}

int binarySearchMarginalCdf(int cdfTextureIndex, int height, float xi)
{
    int lo = 0;
    int hi = height - 1;
    for (int i = 0; i < 20 && lo < hi; i++) {
        int mid = (lo + hi) >> 1;
        float c = textureSamplers[cdfTextureIndex].Load(int3(int2(0, mid), 0)).g;
        if (c < xi)
            lo = mid + 1;
        else
            hi = mid;
    }
    return clamp(lo, 0, height - 1);
}

int binarySearchConditionalCdf(int cdfTextureIndex, int width, int y, float xi)
{
    int lo = 0;
    int hi = width - 1;
    for (int i = 0; i < 20 && lo < hi; i++) {
        int mid = (lo + hi) >> 1;
        float c = textureSamplers[cdfTextureIndex].Load(int3(int2(mid, y), 0)).r;
        if (c < xi)
            lo = mid + 1;
        else
            hi = mid;
    }
    return clamp(lo, 0, width - 1);
}

float3 sampleEnvironmentDirection(
    in EnvironmentDataGpu environmentData,
    inout uint rngState,
    out float lightPdf)
{
    lightPdf = 0.0;
    if (environmentData.textureIndex < 0 || environmentData.cdfTextureIndex < 0)
        return float3(0.0);

    uint envWidth;
    uint envHeight;
    textureSamplers[environmentData.textureIndex].GetDimensions(envWidth, envHeight);
    int2 envSize = int2(envWidth, envHeight);
    if (envSize.x <= 0 || envSize.y <= 0)
        return float3(0.0);

    float xi1 = rand(rngState);
    float xi2 = rand(rngState);

    int y = binarySearchMarginalCdf(environmentData.cdfTextureIndex, envSize.y, xi1);
    int x = binarySearchConditionalCdf(environmentData.cdfTextureIndex, envSize.x, y, xi2);
    float4 cdfTexel = textureSamplers[environmentData.cdfTextureIndex].Load(int3(int2(x, y), 0));

    float v = (float(y) + 0.5) / float(envSize.y);
    float u = (float(x) + 0.5) / float(envSize.x);
    float theta = v * PI;
    float phi = (u - 0.5) * (2.0 * PI);
    float sinTheta = max(sin(theta), EPSILON);

    float uvPdf = max(cdfTexel.a, 0.0);
    lightPdf = uvPdf / max(2.0 * PI * PI * sinTheta, EPSILON);

    float3 rotatedDir = float3(
        cos(phi) * sinTheta,
        cos(theta),
        sin(phi) * sinTheta);

    float3 worldDir = rotatedDir;
    worldDir.x = rotatedDir.x * environmentData.rotationCos + rotatedDir.z * environmentData.rotationSin;
    worldDir.z = -rotatedDir.x * environmentData.rotationSin + rotatedDir.z * environmentData.rotationCos;
    return fastNormalize(worldDir);
}

struct EnvironmentLight : ILight
{
    LightLiSample sampleLi(float3 referencePoint, float2 u)
    {
        referencePoint = referencePoint;

        LightLiSample sample;
        sample.wi = float3(0.0, 1.0, 0.0);
        sample.Li = float3(0.0);
        sample.pdf = 0.0;

        EnvironmentDataGpu environmentData = sceneSettings.environment;
        if (environmentData.textureIndex < 0 || environmentData.cdfTextureIndex < 0)
            return sample;

        uint rngState = asuint(u.x * 4294967295.0) ^ (asuint(u.y * 4294967295.0) * 747796405u + 2891336453u);
        if (rngState == 0u)
            rngState = 1u;

        sample.wi = sampleEnvironmentDirection(environmentData, rngState, sample.pdf);
        sample.Li = sampleEnvironmentMapColor(sample.wi, environmentData, environmentData.lightingExposureScale);
        return sample;
    }

    float pdfLi(float3 referencePoint, float3 wi)
    {
        referencePoint = referencePoint;

        EnvironmentDataGpu environmentData = sceneSettings.environment;
        if (environmentData.textureIndex < 0 || environmentData.cdfTextureIndex < 0)
            return 0.0;

        float2 uv = directionToEnvironmentUv(wi, environmentData);
        int cdfTex = environmentData.cdfTextureIndex;
        uint width;
        uint height;
        textureSamplers[cdfTex].GetDimensions(width, height);
        float2 texel = float2(1.0 / max(float(width), 1.0), 1.0 / max(float(height), 1.0));
        float rowPdf = textureSamplers[cdfTex].Sample(float2(uv.x, 0.5 * texel.y)).a;
        float conditionalPdf = textureSamplers[cdfTex].Sample(float2(uv.x, uv.y)).r;
        float sinTheta = max(sin(PI * uv.y), EPSILON);
        return max(rowPdf * conditionalPdf / (2.0 * PI * PI * sinTheta), 0.0);
    }

    LightLeSample sampleLe(float2 u1, float2 u2)
    {
        u2 = u2;

        LightLeSample sample;
        sample.pdfPos = 1.0;
        sample.direction = float3(0.0, 1.0, 0.0);
        sample.radiance = float3(0.0);
        sample.pdfDir = 0.0;

        LightLiSample li = sampleLi(float3(0.0), u1);
        sample.direction = li.wi;
        sample.radiance = li.Li;
        sample.pdfDir = li.pdf;
        return sample;
    }
};

#endif // ENVIRONMENT_LIGHT_SLANG
