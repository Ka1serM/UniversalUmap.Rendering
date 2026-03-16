// Common miss shader functions shared across AO, DirectLighting, and PathTracing
#ifndef COMMON_MISS_GLSL
#define COMMON_MISS_GLSL

#include "../SharedStructs.h"
#include "../Bindings.glsl"
#include "../Common.glsl"

bool hasDirectionalLight(in EnvironmentDataGpu environmentData) {
    return environmentData.directionalIntensity > EPSILON &&
           dot(environmentData.directionalDirection, environmentData.directionalDirection) > EPSILON;
}

vec3 getDirectionalLightDirection(in EnvironmentDataGpu environmentData) {
    vec3 d = environmentData.directionalDirection;
    float l2 = dot(d, d);
    if (l2 <= EPSILON)
        return vec3(0.0, 1.0, 0.0);
    return d * inversesqrt(l2);
}

vec2 directionToEnvironmentUv(in vec3 worldRayDirection, in EnvironmentDataGpu environmentData) {
    vec3 rotatedDir = worldRayDirection;
    rotatedDir.x = worldRayDirection.x * environmentData.rotationCos - worldRayDirection.z * environmentData.rotationSin;
    rotatedDir.z = worldRayDirection.x * environmentData.rotationSin + worldRayDirection.z * environmentData.rotationCos;

    vec2 uv;
    uv.x = atan(rotatedDir.z, rotatedDir.x) / (2.0 * PI) + 0.5;
    uv.y = 1.0 - acos(clamp(rotatedDir.y, -1.0, 1.0)) / PI;
    return uv;
}

vec3 sampleEnvironmentMapColor(in vec3 worldRayDirection, in EnvironmentDataGpu environmentData, in float exposureStops) {
    if (environmentData.textureIndex == -1)
        return vec3(1.0);

    vec2 uv = directionToEnvironmentUv(worldRayDirection, environmentData);
    return texture(textureSamplers[environmentData.textureIndex], uv).rgb * exposureStops;
}

vec3 sampleEnvironmentMapColorLod(in vec3 worldRayDirection, in EnvironmentDataGpu environmentData, in float exposureStops, in float lod) {
    if (environmentData.textureIndex == -1)
        return vec3(1.0);

    vec2 uv = directionToEnvironmentUv(worldRayDirection, environmentData);
    return textureLod(textureSamplers[environmentData.textureIndex], uv, max(lod, 0.0)).rgb * exposureStops;
}

float evaluateEnvironmentPdfForDirection(in vec3 worldRayDirection, in EnvironmentDataGpu environmentData) {
    if (environmentData.textureIndex < 0 || environmentData.cdfTextureIndex < 0)
        return 0.0;

    ivec2 envSize = textureSize(textureSamplers[environmentData.textureIndex], 0);
    if (envSize.x <= 0 || envSize.y <= 0)
        return 0.0;

    vec2 uv = directionToEnvironmentUv(worldRayDirection, environmentData);
    int x = clamp(int(floor(uv.x * float(envSize.x))), 0, envSize.x - 1);
    int y = clamp(int(floor(uv.y * float(envSize.y))), 0, envSize.y - 1);
    float uvPdf = max(texelFetch(textureSamplers[environmentData.cdfTextureIndex], ivec2(x, y), 0).a, 0.0);
    float theta = uv.y * PI;
    float sinTheta = max(sin(theta), EPSILON);
    return uvPdf / max(2.0 * PI * PI * sinTheta, EPSILON);
}

float evaluateNeeLightPdfForDirection(in vec3 worldRayDirection, in EnvironmentDataGpu environmentData) {
    return evaluateEnvironmentPdfForDirection(worldRayDirection, environmentData);
}

vec3 evaluateEnvironmentRadiance(in vec3 worldRayDirection, in EnvironmentDataGpu environmentData) {
    vec3 sampledColor = sampleEnvironmentMapColor(worldRayDirection, environmentData, environmentData.lightingExposureScale);
    return sampledColor;
}

vec3 evaluateEnvironmentVisibleRadiance(in vec3 worldRayDirection, in EnvironmentDataGpu environmentData) {
    vec3 sampledColor = sampleEnvironmentMapColor(worldRayDirection, environmentData, environmentData.visibleExposureScale);
    return sampledColor;
}

// PathTracing miss shader
void shadeMiss(in vec3 worldRayDirection, in EnvironmentDataGpu environmentData, in RenderSettingsDataGpu renderSettings, inout Payload payload) {
    vec3 envColor = payload.depth == 0
        ? evaluateEnvironmentVisibleRadiance(worldRayDirection, environmentData)
        : evaluateEnvironmentRadiance(worldRayDirection, environmentData);

    if (payload.depth > 0 && payload.lastBsdfPdf > 0.0) {
        float neeLightPdf = evaluateNeeLightPdfForDirection(worldRayDirection, environmentData);
        float wBsdf = powerHeuristic(payload.lastBsdfPdf, neeLightPdf);
        envColor *= wBsdf;
    }

    payload.attenuation = vec3(1.0);
    payload.emission    = envColor;
    payload.albedo      = envColor;
    payload.normal      = vec3(0.0);

    if ((payload.depth == 0 && environmentData.visible == 0) || (payload.depth == 0 && renderSettings.transparentBackground != 0))
        payload.flags |= ENV_TRANSPARENT;

    payload.flags |= RAY_TERMINATED;
}

void shadeMiss(in vec3 worldRayDirection, in EnvironmentDataGpu environmentData, in RenderSettingsDataGpu renderSettings, inout AoPayload payload) {
    payload.emission = evaluateEnvironmentVisibleRadiance(worldRayDirection, environmentData);
    payload.albedo = payload.emission;
    payload.normal = vec3(0.0);

    if (environmentData.visible == 0 || renderSettings.transparentBackground != 0)
        payload.flags |= ENV_TRANSPARENT;

    payload.flags |= RAY_TERMINATED;
}

#endif // COMMON_MISS_GLSL
