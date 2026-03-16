// Primary ray generation
#ifndef PRIMARY_RAY_GLSL
#define PRIMARY_RAY_GLSL

#include "../SharedStructs.h"
#include "../Common.glsl"

vec2 concentricSampleDisk(float u1, float u2) {
    float offsetX = 2.0 * u1 - 1.0;
    float offsetY = 2.0 * u2 - 1.0;
    if (offsetX == 0.0 && offsetY == 0.0)
        return vec2(0.0);

    float r, theta;
    if (abs(offsetX) > abs(offsetY)) {
        r = offsetX;
        theta = (PI / 4.0) * (offsetY / offsetX);
    } else {
        r = offsetY;
        theta = (PI / 2.0) - (PI / 4.0) * (offsetX / offsetY);
    }
    return r * vec2(cos(theta), sin(theta));
}

vec2 roundBokeh(float u1, float u2, float edgeBias) {
    vec2 diskSample = concentricSampleDisk(u1, u2);
    float r = length(diskSample);
    float newR = pow(r, 1.0 / max(edgeBias, EPSILON));
    if (r > 0.0)
        return diskSample * (newR / r);
    return vec2(0.0);
}

void generatePrimaryRay(
    in ivec2 pixelCoord,
    in ivec2 screenSize,
    in CameraDataGpu camera,
    inout SamplerState samplerState,
    in bool deterministicSample,
    in bool useThinLens,
    out vec3 rayOrigin,
    out vec3 rayDirection
) {
    vec2 jitter = deterministicSample ? vec2(0.0) : (getSample2D(samplerState) - 0.5);
    vec2 uv = (vec2(pixelCoord) + jitter) / vec2(screenSize);
    vec2 sensorOffset = uv - 0.5;

    const vec3 camPos = camera.position;
    const vec3 camDir = normalize(camera.direction);
    const vec3 horizontal = camera.horizontal;
    const vec3 vertical = camera.vertical;
    float focalLength = camera.focalLength;

    vec3 imagePlaneCenter = camPos + camDir * focalLength;
    vec3 imagePlanePoint = imagePlaneCenter + horizontal * sensorOffset.x + vertical * sensorOffset.y;

    rayOrigin = camPos;
    rayDirection = normalize(imagePlanePoint - rayOrigin);

    if (useThinLens && camera.aperture > 0.0) {
        float apertureRadius = camera.aperture;
        vec2 lensSampleRaw = getSample2D(samplerState);
        vec2 lensSample = roundBokeh(lensSampleRaw.x, lensSampleRaw.y, camera.bokehBias) * apertureRadius;
        vec3 lensU = normalize(horizontal);
        vec3 lensV = normalize(vertical);
        vec3 rayOriginDOF = camPos + lensU * lensSample.x + lensV * lensSample.y;
        vec3 focusPoint = rayOrigin + rayDirection * camera.focusDistance;

        rayOrigin = rayOriginDOF;
        rayDirection = normalize(focusPoint - rayOriginDOF);
    }
}

#endif // PRIMARY_RAY_GLSL
