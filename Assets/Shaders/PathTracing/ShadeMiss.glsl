#ifndef SHADE_MISS_GLSL
#define SHADE_MISS_GLSL

#include "../Bindings.glsl"

bool hasDirectionalLight(in EnvironmentData environmentData) {
    return environmentData.directionalIntensity > EPSILON &&
           dot(environmentData.directionalDirection, environmentData.directionalDirection) > EPSILON;
}

vec3 getDirectionalLightDirection(in EnvironmentData environmentData) {
    vec3 d = environmentData.directionalDirection;
    float l2 = dot(d, d);
    if (l2 <= EPSILON)
        return vec3(0.0, 1.0, 0.0);
    return d * inversesqrt(l2);
}

vec2 directionToEnvironmentUv(in vec3 worldRayDirection, in float rotationDegrees) {
    float radRotation = radians(rotationDegrees);
    float s = sin(radRotation);
    float c = cos(radRotation);
    vec3 rotatedDir = normalize(worldRayDirection);
    rotatedDir.x = worldRayDirection.x * c - worldRayDirection.z * s;
    rotatedDir.z = worldRayDirection.x * s + worldRayDirection.z * c;

    vec2 uv;
    uv.x = atan(rotatedDir.z, rotatedDir.x) / (2.0 * PI) + 0.5;
    uv.y = 1.0 - acos(clamp(rotatedDir.y, -1.0, 1.0)) / PI;
    return uv;
}

vec3 sampleEnvironmentMapColor(in vec3 worldRayDirection, in EnvironmentData environmentData, in float exposureStops) {
    if (environmentData.textureIndex == -1)
        return vec3(1.0);

    vec2 uv = directionToEnvironmentUv(worldRayDirection, environmentData.rotation);
    return texture(textureSamplers[environmentData.textureIndex], uv).rgb * exp2(exposureStops);
}

float evaluateEnvironmentPdfForDirection(in vec3 worldRayDirection, in EnvironmentData environmentData) {
    if (environmentData.textureIndex < 0 || environmentData.cdfTextureIndex < 0)
        return 0.0;

    ivec2 envSize = textureSize(textureSamplers[environmentData.textureIndex], 0);
    if (envSize.x <= 0 || envSize.y <= 0)
        return 0.0;

    vec2 uv = directionToEnvironmentUv(worldRayDirection, environmentData.rotation);
    int x = clamp(int(floor(uv.x * float(envSize.x))), 0, envSize.x - 1);
    int y = clamp(int(floor(uv.y * float(envSize.y))), 0, envSize.y - 1);
    float uvPdf = max(texelFetch(textureSamplers[environmentData.cdfTextureIndex], ivec2(x, y), 0).a, 0.0);
    float theta = uv.y * PI;
    float sinTheta = max(sin(theta), EPSILON);
    return uvPdf / max(2.0 * PI * PI * sinTheta, EPSILON);
}

float evaluateNeeLightPdfForDirection(in vec3 worldRayDirection, in EnvironmentData environmentData) {
    return evaluateEnvironmentPdfForDirection(worldRayDirection, environmentData);
}

vec3 evaluateEnvironmentRadiance(in vec3 worldRayDirection, in EnvironmentData environmentData) {
    vec3 sampledColor = sampleEnvironmentMapColor(worldRayDirection, environmentData, environmentData.lightingExposure);
    return sampledColor;
}

vec3 evaluateEnvironmentVisibleRadiance(in vec3 worldRayDirection, in EnvironmentData environmentData) {
    vec3 sampledColor = sampleEnvironmentMapColor(worldRayDirection, environmentData, environmentData.visibleExposure);
    return sampledColor;
}

void shadeMiss(in vec3 worldRayDirection, in EnvironmentData environmentData, inout Payload payload) {
    vec3 envColor = payload.depth == 0
        ? evaluateEnvironmentVisibleRadiance(worldRayDirection, environmentData)
        : evaluateEnvironmentRadiance(worldRayDirection, environmentData);

    // MIS: when the environment is reached via BSDF sampling, apply balance against NEE.
    if (payload.depth > 0 && payload.lastBsdfPdf > 0.0) {
        float neeLightPdf = evaluateNeeLightPdfForDirection(worldRayDirection, environmentData);
        float wBsdf = powerHeuristic(payload.lastBsdfPdf, neeLightPdf);
        envColor *= wBsdf;
    }

    // Always contribute environment light
    payload.attenuation = vec3(1.0);
    payload.emission    = envColor;
    payload.albedo      = envColor;
    payload.normal      = vec3(0.0);

    // If environment is invisible at primary ray → mark transparent background
    if (payload.depth == 0 && environmentData.visible == 0)
        payload.flags |= ENV_TRANSPARENT;

    // Miss always terminates the ray
    payload.flags |= RAY_TERMINATED;
}

#endif
