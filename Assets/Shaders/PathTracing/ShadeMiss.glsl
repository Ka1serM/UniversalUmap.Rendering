#ifndef SHADE_MISS_GLSL
#define SHADE_MISS_GLSL

#include "../Bindings.glsl"

const bool USE_PROCEDURAL_SKY = true;

vec3 sampleEnvironmentMapColor(in vec3 worldRayDirection, in EnvironmentData environmentData) {
    if (environmentData.textureIndex == -1)
        return vec3(1.0);

    float radRotation = radians(environmentData.rotation);
    float s = sin(radRotation);
    float c = cos(radRotation);
    vec3 rotatedDir = worldRayDirection;
    rotatedDir.x = worldRayDirection.x * c - worldRayDirection.z * s;
    rotatedDir.z = worldRayDirection.x * s + worldRayDirection.z * c;

    vec3 viewDir = normalize(rotatedDir);
    vec2 uv;
    uv.x = atan(viewDir.z, viewDir.x) / (2.0 * PI) + 0.5;
    uv.y = 1.0 - acos(clamp(viewDir.y, -1.0, 1.0)) / PI;
    return texture(textureSamplers[environmentData.textureIndex], uv).rgb * exp2(environmentData.exposure);
}

vec3 sampleProceduralSkyColor(in vec3 worldRayDirection) {
    vec3 d = normalize(worldRayDirection);
    float t = clamp(d.y * 0.5 + 0.5, 0.0, 1.0);

    // Bright blue zenith and near-white horizon for a clear sky look.
    vec3 horizon = vec3(1.22, 1.28, 1.34);
    vec3 skyTop = vec3(0.24, 0.62, 1.45) * 2.1;
    vec3 base = mix(horizon, skyTop, smoothstep(0.0, 1.0, t));

    // Extra horizon glow to keep lower hemisphere bright.
    float horizonGlow = exp(-abs(d.y) * 10.0);
    base += vec3(0.55, 0.72, 0.95) * horizonGlow;

    // Soft sun lobe.
    vec3 sunDir = normalize(vec3(0.2, 0.95, 0.25));
    float sun = pow(max(dot(d, sunDir), 0.0), 320.0);
    vec3 sunColor = vec3(1.0, 0.99, 0.94) * 48.0 * sun;

    return base + sunColor;
}

vec3 evaluateEnvironmentRadiance(in vec3 worldRayDirection, in EnvironmentData environmentData) {
    vec3 sampledColor = USE_PROCEDURAL_SKY
        ? sampleProceduralSkyColor(worldRayDirection)
        : sampleEnvironmentMapColor(worldRayDirection, environmentData);
    return sampledColor * environmentData.color * environmentData.intensity;
}

void shadeMiss(in vec3 worldRayDirection, in EnvironmentData environmentData, inout Payload payload) {
    vec3 envColor = evaluateEnvironmentRadiance(worldRayDirection, environmentData);

    // MIS: when the environment is reached via BSDF sampling, apply balance against NEE.
    if (payload.depth > 0 && payload.lastBsdfPdf > 0.0 && payload.lastNeeLightPdf > 0.0) {
        float wBsdf = powerHeuristic(payload.lastBsdfPdf, payload.lastNeeLightPdf);
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
